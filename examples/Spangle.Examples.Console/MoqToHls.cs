using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Extensions.Moqt;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.MsQuic;
using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle.Examples.Console;

/// <summary>
/// MOQT in, HLS out: dials a relay, subscribes to a namespace's catalog, and republishes its LOC
/// tracks as HLS into <c>hls-out/</c> — the ingest mirror of <see cref="RtmpToMoq"/>. Point it at a
/// namespace another publisher (Spangle's own egress, or a browser broadcaster) is producing.
/// <para>
/// <c>SPANGLE_MOQ_RELAY</c> (host:port) and <c>SPANGLE_MOQ_NAMESPACE</c> (e.g. <c>live/test</c>)
/// select the source; the relay's certificate is not validated (a development relay).
/// </para>
/// </summary>
internal sealed class MoqToHls
{
    private readonly ILogger<MoqToHls> _logger;

    public MoqToHls(ILogger<MoqToHls> logger) => _logger = logger;

    // Do NOT use this code in production!
    public async ValueTask StartAsync()
    {
        string relay = Environment.GetEnvironmentVariable("SPANGLE_MOQ_RELAY") ?? "127.0.0.1:4433";
        string @namespace = Environment.GetEnvironmentVariable("SPANGLE_MOQ_NAMESPACE") ?? "live/test";
        string[] parts = relay.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        string[] namespaceFields = @namespace.Split('/');
        string streamKey = namespaceFields[^1];

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        CancellationToken ct = cts.Token;

        if (!MsQuicTransport.Shared.IsSupported)
        {
            _logger.ZLogError($"msquic is not available on this host; cannot dial a relay.");
            return;
        }

        await using IQuicConnection connection = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = remote,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = "localhost",
            AllowUntrustedCertificates = true,
        }, ct);

        var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path, Encoding.UTF8.GetBytes("/moq"))]);
        await using MoqSession session = await MoqSession.ConnectAsync(connection, setup, cancellationToken: ct);
        _logger.ZLogInformation($"MOQT ingest: connected to {remote}; pulling '{@namespace}' -> hls-out/{streamKey}.");

        using var receiver = new MoqReceiverContext(session, namespaceFields, streamKey, remote, LocDraft.Draft01, TimeSpan.FromSeconds(30), ct);
        var hls = new HLSSenderContext(ct) { OutputDirectory = "hls-out" };
        using var live = new LiveContext(receiver, hls, cancellationToken: ct);
        using var sender = new HLSSender();

        Task senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.StartAsync(hls);
            }
            catch (Exception e)
            {
                _logger.ZLogError($"HLS sender error: {e}");
            }
        }, CancellationToken.None);

        try
        {
            await live.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception e)
        {
            _logger.ZLogError($"MOQT ingest error: {e}");
        }
        finally
        {
            await senderTask;
            _logger.ZLogInformation($"MOQT ingest closed.");
        }
    }
}
