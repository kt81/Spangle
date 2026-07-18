using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using ZLogger;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Everything <see cref="MoqSender"/> holds for exactly one relay connection: the QUIC
/// connection, the MOQT session, the three published tracks, and the tasks pumping them.
/// It exists to make partial failure impossible to hold wrong — either
/// <see cref="ConnectAsync"/> returns a fully working connection, or it releases whatever it
/// had built and throws, so the sender never sees a half-connected state it must remember to
/// unwind. (Its predecessor spread this across eight nullable fields on the sender; a failed
/// PUBLISH_NAMESPACE left the session set but the demux loop unborn, and the sender wedged
/// forever — no teardown condition could fire, no reconnect gate could open, and every frame
/// was silently dropped as "no subscriber". A failure between the dial and the SETUP leaked
/// one QUIC connection per retry, too.)
/// </summary>
internal sealed class MoqRelayConnection : IAsyncDisposable
{
    private static readonly ILogger<MoqRelayConnection> s_logger = SpangleLogManager.GetLogger<MoqRelayConnection>();

    private readonly IQuicConnection _connection;
    private readonly MoqSession _session;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _demux;
    private Task? _catalogLoop;

    private MoqRelayConnection(IQuicConnection connection, MoqSession session, CancellationTokenSource lifetime,
        Task demux, MoqCatalogTrack catalogTrack, MoqFrameTrack video, MoqFrameTrack audio)
    {
        _connection = connection;
        _session = session;
        _lifetime = lifetime;
        _demux = demux;
        CatalogTrack = catalogTrack;
        Video = video;
        Audio = audio;
    }

    internal MoqCatalogTrack CatalogTrack { get; }
    internal MoqFrameTrack Video { get; }
    internal MoqFrameTrack Audio { get; }

    /// <summary>
    /// Whether the connection has died under us. The demux loop ends only when the connection
    /// under it has: that is how a dead relay announces itself.
    /// </summary>
    internal bool IsDead => _demux.IsCompleted;

    /// <summary>
    /// Dials the relay, performs the SETUP handshake, declares the tracks, announces the
    /// namespace, and starts the demux loop — or releases everything it had built and throws.
    /// </summary>
    internal static async Task<MoqRelayConnection> ConnectAsync(MoqSenderContext context, CancellationToken ct)
    {
        MoqSenderOptions options = context.Options;
        string[] namespaceFields = context.ResolveNamespaceFields();

        IQuicConnection connection = await context.Transport.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = options.Relay,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = options.TargetHost,
            AllowUntrustedCertificates = options.AllowUntrustedRelayCertificate,
            // Announced and silent is this publisher's normal state; without this the relay drops
            // the connection and forgets the namespace while we wait for a first subscriber.
            KeepAliveInterval = options.KeepAliveInterval,
        }, ct).ConfigureAwait(false);

        MoqSession? session = null;
        CancellationTokenSource? lifetime = null;
        try
        {
            var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path,
                Encoding.UTF8.GetBytes(options.Path))]);
            session = await MoqSession.ConnectAsync(connection, setup, cancellationToken: ct).ConfigureAwait(false);

            MoqPublisher publisher = MoqPublisher.Create(session);
            TrackNamespace @namespace = TrackNamespace.FromStrings(namespaceFields);

            // Group ids have to be unique for the life of the track, which outlives this session: a
            // relay caches by group id and a restarted publisher that began again at 0 would be
            // republishing group 0 with different content. It resolves that collision by dropping the
            // subscriber — every viewer, until its cache expires. Wall-clock milliseconds is the
            // spec's own suggestion (MSF §6.1) and the only thing available that a restart cannot
            // repeat.
            var firstGroupId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var catalogTrack =
                new MoqCatalogTrack(publisher.PublishTrack(MoqCatalogTrack.NameIn(@namespace)), firstGroupId);
            var video = new MoqFrameTrack(publisher.PublishTrack(Track(context, namespaceFields, "video0")),
                options.StreamMapping, firstGroupId);
            var audio = new MoqFrameTrack(publisher.PublishTrack(Track(context, namespaceFields, "audio0")),
                options.StreamMapping, firstGroupId);

            await publisher.AnnounceNamespaceAsync(@namespace, ct).ConfigureAwait(false);
            s_logger.ZLogInformation(
                $"MOQT: announced '{string.Join('/', namespaceFields)}' to {options.Relay}; serving subscriptions.");

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task demux = publisher.RunAsync(lifetime.Token);

            // Nothing is published until a subscriber asks, so "is anyone watching, and under what
            // alias" is the first question when a viewer sees nothing.
            LogFirstSubscriber("catalog", catalogTrack.WaitForSubscriberAsync());
            LogFirstSubscriber(options.TrackNamePrefix + "video0", video.WaitForSubscriberAsync());
            LogFirstSubscriber(options.TrackNamePrefix + "audio0", audio.WaitForSubscriberAsync());

            return new MoqRelayConnection(connection, session, lifetime, demux, catalogTrack, video, audio);
        }
        catch
        {
            // Whatever was built belongs to a connection that never became usable. Releasing it
            // here — not in the caller — is the whole point of this class.
            lifetime?.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts the per-connection catalog loop. It is given this connection's lifetime token, so
    /// it stops when the connection is abandoned or disposed.
    /// </summary>
    internal void BeginPublishingCatalog(Func<CancellationToken, Task> loop) =>
        _catalogLoop = Task.Run(() => loop(_lifetime.Token), CancellationToken.None);

    /// <summary>
    /// Releases a connection that is already gone so a new one can be dialed: cancel the loops,
    /// abandon the open groups, and drop the transport objects. Nothing here says goodbye to the
    /// peer — there is no peer; the goodbye path is <see cref="DisposeAsync"/>.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every resource being released belongs to a connection that is already dead.")]
    internal async ValueTask AbandonAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await WaitForLoopsAsync().ConfigureAwait(false);

        await Video.AbandonGroupAsync().ConfigureAwait(false);
        await Audio.AbandonGroupAsync().ConfigureAwait(false);
        await CatalogTrack.AbandonGroupAsync().ConfigureAwait(false);

        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // disposing the corpse of a connection is best-effort by definition
        }

        _lifetime.Dispose();
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification =
            "Shutdown: a peer that has already gone is the ordinary case and must not mask the reason we are stopping.")]
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            // Close the open groups before the session goes: a subscriber that never hears a group
            // ended waits out a timeout on it.
            await Video.DisposeAsync().ConfigureAwait(false);
            await Audio.DisposeAsync().ConfigureAwait(false);
            await CatalogTrack.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            s_logger.ZLogDebug($"MOQT: closing the tracks failed on shutdown: {e.Message}");
        }

        await WaitForLoopsAsync().ConfigureAwait(false);

        await _session.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Both loops stop by cancellation, and either may report the death that brought us here.")]
    private async Task WaitForLoopsAsync()
    {
        foreach (Task? task in new[] { _catalogLoop, _demux })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // both stop by cancellation, and either may report the death that brought us here
            }
        }
    }

    private static FullTrackName Track(MoqSenderContext context, string[] namespaceFields, string name) =>
        FullTrackName.FromStrings(namespaceFields, context.Options.TrackNamePrefix + name);

    [SuppressMessage("Reliability", "CA2008:Do not create tasks without passing a TaskScheduler",
        Justification = "A continuation that writes one log line; the scheduler is immaterial.")]
    private static void LogFirstSubscriber(string track, Task<ulong> subscribed) =>
        _ = subscribed.ContinueWith(
            t => s_logger.ZLogInformation($"MOQT: '{track}' has a subscriber (alias {t.Result})."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
}
