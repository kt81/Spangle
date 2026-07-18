using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Net.Moqt;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Ingests a MOQT stream: subscribes to a namespace's catalog, discovers its tracks, subscribes to
/// each, and turns their LOC objects back into Spangle's canonical MediaFrames — so a MOQT source
/// republishes through the same HLS/CMAF path an RTMP or SRT publisher does. This is the pull side
/// of the MOQT support (Spangle dials the relay, as it does for an RTSP pull source), the mirror of
/// <see cref="MoqSender"/>'s egress.
/// <para>
/// Scope: one video and one audio LOC track, discovered by role from the catalog. A subscriber
/// joins at the live edge, so playable media begins at the next keyframe (the one that carries the
/// decoder configuration). The session is supplied established; this context owns the subscribe and
/// decode, not the dial.
/// </para>
/// </summary>
public sealed class MoqReceiverContext : ReceiverContextBase<MoqReceiverContext>, IDisposable
{
    private static readonly ILogger<MoqReceiverContext> s_logger =
        SpangleLogManager.GetLogger<MoqReceiverContext>();

    private readonly MoqSession _session;
    private readonly string[] _namespaceFields;
    private readonly LocDraft _draft;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationToken _hostToken;
    // One writer, many producers: the video and audio drain loops share the single MediaOutlet.
    private readonly SemaphoreSlim _outletGate = new(1, 1);
    private CancellationTokenSource? _watchdog;
    private volatile string? _restartReason;
    private volatile bool _completed;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override EndPoint EndPoint { get; }

    /// <inheritdoc />
    public override string? StreamName { get; }

    /// <inheritdoc />
    public override bool IsCompleted => _completed;

    /// <summary>
    /// Creates a receiver over an established <paramref name="session"/>, ingesting the namespace
    /// named by <paramref name="namespaceFields"/> and republishing under <paramref name="streamName"/>.
    /// <paramref name="draft"/> is the LOC draft the source publishes in.
    /// </summary>
    public MoqReceiverContext(MoqSession session, string[] namespaceFields, string streamName, EndPoint endPoint,
        LocDraft draft, TimeSpan readTimeout, CancellationToken ct)
        : this(new Pipe(), session, namespaceFields, streamName, endPoint, draft, readTimeout, ct)
    {
    }

    // MOQT has no byte-pipe transport to expose (its "connection" is a set of QUIC streams), so
    // the base's RemoteReader/Writer — meant for protocols that need raw connection access — are
    // a dummy pipe nothing reads. One pipe per instance: it carries nothing today, but a shared
    // static one would crosstalk the moment any host code touched RemoteWriter.
    private MoqReceiverContext(Pipe dummy, MoqSession session, string[] namespaceFields, string streamName,
        EndPoint endPoint, LocDraft draft, TimeSpan readTimeout, CancellationToken ct)
        : base(dummy.Reader, dummy.Writer, ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(namespaceFields);
        _session = session;
        _namespaceFields = namespaceFields;
        _draft = draft;
        _readTimeout = readTimeout;
        // The host later swaps CancellationToken for the watchdog's token; keeping the
        // original apart is what lets a timeout be told from an ordinary shutdown.
        _hostToken = ct;
        StreamName = streamName;
        EndPoint = endPoint;
        Id = streamName;
    }

    /// <inheritdoc />
    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        ArgumentNullException.ThrowIfNull(readTimeoutSource);
        CancellationToken ct = CancellationToken;

        // Publish authorization and same-name takeover before any media is consumed — the pull
        // side must hold the same gate a push publisher would, or an RTMP session and a pulled
        // stream sharing a key would write the same HLS output at once.
        if (PublishGate is { } gate && !await gate.TryOpenAsync(StreamName ?? Id, ct).ConfigureAwait(false))
        {
            s_logger.ZLogWarning($"MOQT ingest: publish rejected for '{StreamName ?? Id}'.");
            _completed = true;
            return;
        }

        MoqSubscriber subscriber = MoqSubscriber.Create(_session);

        // The watchdog doubles as the restart lever: the catalog watcher pulls it too, so it
        // is kept even with the timeout disabled. Armed here (a catalog that never comes is a
        // stall too) and re-armed by every media object; when it fires, the read token
        // cancels, this method ends, and the host's redial loop takes over.
        _watchdog = readTimeoutSource;
        if (_readTimeout > TimeSpan.Zero)
        {
            readTimeoutSource.CancelAfter(_readTimeout);
        }

        // The session's demux loop: nothing arrives — no catalog, no media — unless it runs.
        // Its end is how the session's death announces itself; the drains follow because their
        // channels complete with it.
        using var demuxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task demux = _session.RunAsync(demuxCts.Token);

        FullTrackName catalogTrack = MoqCatalogTrack.NameIn(TrackNamespace.FromStrings(_namespaceFields));
        MoqSubscription catalogSubscription = await subscriber.SubscribeAsync(catalogTrack, ct).ConfigureAwait(false);
        IAsyncEnumerator<MoqObject> catalogObjects =
            catalogSubscription.ReadObjectsAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            MsfCatalog? catalog = await NextCatalogAsync(catalogObjects).ConfigureAwait(false);
            if (catalog is null)
            {
                s_logger.ZLogWarning($"MOQT ingest: no catalog for '{string.Join('/', _namespaceFields)}'; nothing to pull.");
                return;
            }

            // Prefer a track whose codec we can actually decode; fall back to the first by
            // role so an unsupported one is reported rather than silently unconsidered.
            MsfTrack? video = catalog.Tracks.FirstOrDefault(t => IsVideo(t) && MapVideo(t.Codec) is not null)
                              ?? catalog.Tracks.FirstOrDefault(IsVideo);
            MsfTrack? audio = catalog.Tracks.FirstOrDefault(t => IsAudio(t) && MapAudio(t.Codec) is not null)
                              ?? catalog.Tracks.FirstOrDefault(IsAudio);

            if (video is { } uv && MapVideo(uv.Codec) is null)
            {
                // A video track we cannot decode must not become a silent, healthy-looking
                // session that outputs nothing forever: say so, and ingest what remains.
                s_logger.ZLogError(
                    $"MOQT ingest: video codec '{uv.Codec}' is not supported; {(audio is null ? "nothing to pull" : "pulling audio only")}.");
                video = null;
            }

            if (audio is { } ua && MapAudio(ua.Codec) is null)
            {
                s_logger.ZLogWarning($"MOQT ingest: audio codec '{ua.Codec}' is not supported; pulling without audio.");
                audio = null;
            }

            if (video is null && audio is null)
            {
                s_logger.ZLogWarning($"MOQT ingest: the catalog declares no usable LOC track; nothing to pull.");
                return;
            }

            // Set the codecs the discovery found: the codec event wires the pipeline and gives us a
            // MediaOutlet. Video first, so a normal A/V stream wires on video (the audio codec would
            // wire too, but only IsAudioOnly makes audio the trigger).
            if (video is null)
            {
                IsAudioOnly = true;
            }

            if (video is { } v)
            {
                VideoWidth = v.Width ?? 0;
                VideoHeight = v.Height ?? 0;
                VideoCodec = MapVideo(v.Codec)!.Value;
            }

            if (audio is { } a)
            {
                AudioCodec = MapAudio(a.Codec)!.Value;
            }

            var drains = new List<Task>(3);
            if (video is { } vt && VideoCodec is { } vc)
            {
                drains.Add(DrainAsync(subscriber, vt, MediaFrameKind.Video, (uint)vc, ct));
            }

            if (audio is { } at && AudioCodec is { } ac)
            {
                drains.Add(DrainAsync(subscriber, at, MediaFrameKind.Audio, (uint)ac, ct));
            }

            // Keep reading the catalog beside the media: a track that appears (or vanishes)
            // later would otherwise stay invisible until something else killed the session —
            // the audio config landing milliseconds after a subscriber grabbed a video-only
            // catalog was a permanent mute before this.
            drains.Add(WatchCatalogAsync(catalogObjects, CatalogShape(catalog), ct));

            s_logger.ZLogInformation($"MOQT ingest: pulling '{string.Join('/', _namespaceFields)}' ({drains.Count - 1} track(s)).");
            await Task.WhenAll(drains).ConfigureAwait(false);

            // The drains only end without cancellation when the session ended under them;
            // the demux task holds the reason, and the host's redial loop wants it.
            await demux.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_hostToken.IsCancellationRequested)
        {
            // orderly shutdown
        }
        catch (OperationCanceledException) when (_restartReason is not null)
        {
            // logged where the restart was decided; the host redials and picks up the change
        }
        catch (OperationCanceledException)
        {
            s_logger.ZLogWarning(
                $"MOQT ingest: no media from '{string.Join('/', _namespaceFields)}' for {_readTimeout.TotalSeconds:0}s; redialling.");
        }
        finally
        {
            _completed = true;
            await catalogObjects.DisposeAsync().ConfigureAwait(false);
            await catalogSubscription.DisposeAsync().ConfigureAwait(false);
            await demuxCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await demux.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the reason, when it matters, was surfaced by the drains or the await above
            }
        }
    }

    /// <summary>Advances the catalog enumerator to the next parseable catalog, or null at the end.</summary>
    private async Task<MsfCatalog?> NextCatalogAsync(IAsyncEnumerator<MoqObject> catalogObjects)
    {
        while (await catalogObjects.MoveNextAsync().ConfigureAwait(false))
        {
            MoqObject moqObject = catalogObjects.Current;
            // Independent catalogs are the group's normal object; the End of Group marker that
            // follows carries no payload.
            if (moqObject.Status != MoqObjectStatus.Normal || moqObject.Payload.IsEmpty)
            {
                continue;
            }

            try
            {
                return MsfCatalog.Parse(moqObject.Payload.Span, string.Join('/', _namespaceFields));
            }
            catch (InvalidDataException e)
            {
                s_logger.ZLogWarning($"MOQT ingest: catalog did not parse ({e.Message}); waiting for the next.");
            }
        }

        return null;
    }

    // Watches the already-open catalog subscription for a change in the usable track set. On
    // one, the cleanest reaction is a restart: the pipeline wires its tracks once, so a track
    // added mid-session cannot be spliced in — but a redial discovers the new shape from
    // scratch and comes back with it.
    private async Task WatchCatalogAsync(IAsyncEnumerator<MoqObject> catalogObjects, string initialShape,
        CancellationToken ct)
    {
        while (await NextCatalogAsync(catalogObjects).ConfigureAwait(false) is { } catalog)
        {
            ct.ThrowIfCancellationRequested();
            string shape = CatalogShape(catalog);
            if (shape == initialShape)
            {
                continue;
            }

            _restartReason = $"the catalog changed its tracks ([{initialShape}] -> [{shape}])";
            s_logger.ZLogInformation($"MOQT ingest: {_restartReason}; re-pulling to pick it up.");
            if (_watchdog is { } lever)
            {
                await lever.CancelAsync().ConfigureAwait(false);
            }

            return;
        }
    }

    // What of a catalog matters for "did the stream change shape": the usable tracks and their
    // codecs, order-independent. GeneratedAt changes on every republication and must not count.
    private static string CatalogShape(MsfCatalog catalog) =>
        string.Join(",", catalog.Tracks
            .Where(t => IsVideo(t) || IsAudio(t))
            .Select(t => $"{t.Name}:{t.Codec}")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Subscribes to one media track and decodes its objects into MediaFrames.</summary>
    private async Task DrainAsync(MoqSubscriber subscriber, MsfTrack track, MediaFrameKind kind, uint codec,
        CancellationToken ct)
    {
        var decoder = new LocMediaDecoder(kind, codec, _draft, DecodeInitData(track));
        FullTrackName name = FullTrackName.FromStrings(_namespaceFields, track.Name);
        MoqSubscription subscription = await subscriber.SubscribeAsync(name, ct).ConfigureAwait(false);
        await using (subscription.ConfigureAwait(false))
        {
            await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct).ConfigureAwait(false))
            {
                if (moqObject.Status != MoqObjectStatus.Normal)
                {
                    continue; // End of Group / End of Track markers carry no media
                }

                PipeWriter? outlet = MediaOutlet;
                if (outlet is null)
                {
                    continue; // not wired yet; a keyframe will come round again
                }

                await _outletGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    decoder.Decode(moqObject, outlet);
                    await outlet.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _outletGate.Release();
                }

                // Progress: every media object pushes the watchdog's deadline out again.
                if (_readTimeout > TimeSpan.Zero)
                {
                    _watchdog?.CancelAfter(_readTimeout);
                }
            }
        }
    }

    private static bool IsVideo(MsfTrack track) =>
        track.Packaging == MsfPackaging.Loc
        && (track.Role == MsfTrackRole.Video || (track.Role is null && MapVideo(track.Codec) is not null));

    private static bool IsAudio(MsfTrack track) =>
        track.Packaging == MsfPackaging.Loc
        && (track.Role == MsfTrackRole.Audio || (track.Role is null && MapAudio(track.Codec) is not null));

    // RFC 6381 / WebCodecs codec strings to Spangle's codec enum, by the prefix that identifies the
    // codec family (the profile/level suffix is the decoder configuration's job, not selection's).
    private static Spangle.VideoCodec? MapVideo(string? codec) => codec switch
    {
        null => null,
        _ when codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
               || codec.StartsWith("avc3", StringComparison.OrdinalIgnoreCase) => Spangle.VideoCodec.H264,
        _ when codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
               || codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) => Spangle.VideoCodec.H265,
        _ when codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) => Spangle.VideoCodec.AV1,
        _ => null,
    };

    private static Spangle.AudioCodec? MapAudio(string? codec) => codec switch
    {
        null => null,
        _ when codec.StartsWith("mp4a.40", StringComparison.OrdinalIgnoreCase) => Spangle.AudioCodec.AAC,
        _ when codec.Equals("opus", StringComparison.OrdinalIgnoreCase) => Spangle.AudioCodec.Opus,
        _ => null,
    };

    private static ReadOnlyMemory<byte> DecodeInitData(MsfTrack track)
    {
        if (track.InitData is not { Length: > 0 } base64)
        {
            return default;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException e)
        {
            s_logger.ZLogWarning($"MOQT ingest: track '{track.Name}' has invalid initData ({e.Message}); ignoring it.");
            return default;
        }
    }

    /// <summary>Diagnostic label for logs, formatted like the other receivers'.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"MoqReceiverContext({Id} from {EndPoint})");

    /// <inheritdoc />
    public void Dispose() => _outletGate.Dispose();
}
