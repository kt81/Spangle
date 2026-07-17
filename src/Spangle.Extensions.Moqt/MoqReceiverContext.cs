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
    // One writer, many producers: the video and audio drain loops share the single MediaOutlet.
    private readonly SemaphoreSlim _outletGate = new(1, 1);
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
        LocDraft draft, CancellationToken ct)
        // MOQT has no byte-pipe transport to expose (its "connection" is a set of QUIC streams), so
        // the base's RemoteReader/Writer — meant for protocols that need raw connection access —
        // are a dummy pipe nothing reads.
        : base(DummyPipe.Reader, DummyPipe.Writer, ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(namespaceFields);
        _session = session;
        _namespaceFields = namespaceFields;
        _draft = draft;
        StreamName = streamName;
        EndPoint = endPoint;
        Id = streamName;
    }

    private static Pipe DummyPipe { get; } = new();

    /// <inheritdoc />
    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        ArgumentNullException.ThrowIfNull(readTimeoutSource);
        CancellationToken ct = CancellationToken;
        MoqSubscriber subscriber = MoqSubscriber.Create(_session);

        try
        {
            MsfCatalog? catalog = await ReadCatalogAsync(subscriber, ct).ConfigureAwait(false);
            if (catalog is null)
            {
                s_logger.ZLogWarning($"MOQT ingest: no catalog for '{string.Join('/', _namespaceFields)}'; nothing to pull.");
                return;
            }

            MsfTrack? video = catalog.Tracks.FirstOrDefault(IsVideo);
            MsfTrack? audio = catalog.Tracks.FirstOrDefault(IsAudio);
            if (video is null && audio is null)
            {
                s_logger.ZLogWarning($"MOQT ingest: the catalog declares no LOC audio or video track.");
                return;
            }

            // Set the codecs the discovery found: the codec event wires the pipeline and gives us a
            // MediaOutlet. Video first, so a normal A/V stream wires on video (the audio codec would
            // wire too, but only IsAudioOnly makes audio the trigger).
            if (video is null)
            {
                IsAudioOnly = true;
            }

            if (video is { } v && MapVideo(v.Codec) is { } videoCodec)
            {
                VideoWidth = v.Width ?? 0;
                VideoHeight = v.Height ?? 0;
                VideoCodec = videoCodec;
            }

            if (audio is { } a && MapAudio(a.Codec) is { } audioCodec)
            {
                AudioCodec = audioCodec;
            }

            var drains = new List<Task>(2);
            if (video is { } vt && VideoCodec is { } vc)
            {
                drains.Add(DrainAsync(subscriber, vt, MediaFrameKind.Video, (uint)vc, ct));
            }

            if (audio is { } at && AudioCodec is { } ac)
            {
                drains.Add(DrainAsync(subscriber, at, MediaFrameKind.Audio, (uint)ac, ct));
            }

            s_logger.ZLogInformation($"MOQT ingest: pulling '{string.Join('/', _namespaceFields)}' ({drains.Count} track(s)).");
            await Task.WhenAll(drains).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // orderly shutdown
        }
        finally
        {
            _completed = true;
        }
    }

    /// <summary>Subscribes to the catalog track and returns the first complete catalog it carries.</summary>
    private async Task<MsfCatalog?> ReadCatalogAsync(MoqSubscriber subscriber, CancellationToken ct)
    {
        FullTrackName catalogTrack = MoqCatalogTrack.NameIn(TrackNamespace.FromStrings(_namespaceFields));
        MoqSubscription subscription = await subscriber.SubscribeAsync(catalogTrack, ct).ConfigureAwait(false);
        await using (subscription.ConfigureAwait(false))
        {
            await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct).ConfigureAwait(false))
            {
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
        }

        return null;
    }

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
