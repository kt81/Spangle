using System.Globalization;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Text;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.MsQuic;

namespace Spangle.Extensions.Moqt.Hosting;

/// <summary>
/// One MOQT pull source: dials the relay over raw QUIC, completes the SETUP handshake as a
/// subscriber, and hands back a <see cref="MoqReceiverContext"/> pulling the configured namespace.
/// The host owns the reconnect loop, so each <see cref="ConnectAsync"/> builds a fresh connection.
/// </summary>
internal sealed class MoqIngestSource : IIngestSource
{
    private readonly MoqIngestSourceOptions _source;
    private readonly MoqEgressOptions _shared;
    private readonly IPEndPoint _relay;
    private readonly string[] _namespaceFields;

    public MoqIngestSource(MoqIngestSourceOptions source, MoqEgressOptions shared)
    {
        _source = source;
        _shared = shared;
        _relay = IPEndPoint.Parse(source.Relay);
        _namespaceFields = source.Namespace.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public string Name => _source.Name;

    public TimeSpan ReconnectDelay => _source.ReconnectDelay;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The receiver and session are owned by the returned IIngestConnection and disposed there.")]
    public async ValueTask<IIngestConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!MsQuicTransport.Shared.IsSupported)
        {
            throw new InvalidOperationException("msquic is unavailable on this host; cannot dial a MOQT relay.");
        }

        IQuicConnection connection = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = _relay,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = _shared.TargetHost,
            AllowUntrustedCertificates = _shared.AllowUntrustedRelayCertificate,
            // A subscriber waiting on a paused publisher is silent by design; without the
            // PING the idle timeout reads that silence as death and the redial loop spins.
            KeepAliveInterval = _source.KeepAliveInterval,
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path,
                Encoding.UTF8.GetBytes(_shared.Path))]);
            MoqSession session = await MoqSession.ConnectAsync(connection, setup, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var receiver = new MoqReceiverContext(session, _namespaceFields, _source.Name, _relay,
                _source.Loc, _source.ReadTimeout, cancellationToken);
            return new MoqIngestConnection(connection, session, receiver);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The transport under one pulled connection: disposing it after the session ends closes the
    // subscriber's streams and the QUIC connection, before the host redials.
    private sealed class MoqIngestConnection(IQuicConnection connection, MoqSession session,
        MoqReceiverContext receiver) : IIngestConnection
    {
        public IReceiverContext Receiver => receiver;

        public async ValueTask DisposeAsync()
        {
            receiver.Dispose();
            await session.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"MoqIngestSource({_source.Name} <- {_relay}/{_source.Namespace})");
}
