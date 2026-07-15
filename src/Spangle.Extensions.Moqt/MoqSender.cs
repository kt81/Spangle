using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AAC;
using Spangle.Codecs.Opus;
using Spangle.Containers.ISOBMFF;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.MsQuic;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// Publishes a Spangle stream to a MOQT relay: every encoded frame becomes one LOC object on its
/// track, and an MSF catalog beside them says what the tracks are. This is the egress that reaches
/// a browser — a player is given the relay and the namespace, reads the catalog, and subscribes.
/// <para>
/// Frames are dropped, never queued, while nobody is subscribed. The source is live and does not
/// slow down for us: waiting for a subscriber inside the read loop would push back through the pipe
/// onto the receiver and stall the ingest itself. Video resumes at the next keyframe, since a group
/// that opened on a delta frame is a group no subscriber can decode.
/// </para>
/// <para>
/// Scope: one video and one audio track, LOC packaging, no ABR ladder and no timeline track. The
/// stream's own name does not appear anywhere yet — see <see cref="MoqSenderOptions.Namespace"/>
/// and <see cref="MoqSenderOptions.TrackNamePrefix"/>, which the host sets per stream.
/// </para>
/// </summary>
public sealed class MoqSender : ISender<MoqSenderContext>, IAsyncDisposable
{
    private static readonly ILogger<MoqSender> s_logger = SpangleLogManager.GetLogger<MoqSender>();

    // A frame is copied out of the pipe before it is published, because the publish is awaited and
    // the pipe's memory is only ours until AdvanceTo. One writer, reused: the copy is done with the
    // previous publish by the time the next frame arrives.
    private readonly ArrayBufferWriter<byte> _frame = new(64 * 1024);

    private IQuicConnection? _connection;
    private MoqSession? _session;
    private MoqCatalogTrack? _catalogTrack;
    private MoqFrameTrack? _video;
    private MoqFrameTrack? _audio;
    private Task? _demux;
    private Task? _catalogLoop;
    private CancellationTokenSource? _lifetime;

    private byte[]? _videoConfig;
    private byte[]? _audioConfig;
    private VideoCodec _videoCodec;
    private AudioCodec _audioCodec;
    private volatile MsfCatalog? _catalog;
    private bool _videoStarted;
    private bool _warnedNoVideoConfig;

    /// <inheritdoc />
    public async ValueTask StartAsync(MoqSenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken ct = context.CancellationToken;
        PipeReader reader = context.IntakeReader;

        try
        {
            await ConnectAsync(context, ct).ConfigureAwait(false);
            await ReadAsync(context, reader, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the normal shutdown path
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(MoqSenderContext context, CancellationToken ct)
    {
        MoqSenderOptions options = context.Options;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _connection = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = options.Relay,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = options.TargetHost,
            AllowUntrustedCertificates = options.AllowUntrustedRelayCertificate,
            // Announced and silent is this publisher's normal state; without this the relay drops
            // the connection and forgets the namespace while we wait for a first subscriber.
            KeepAliveInterval = options.KeepAliveInterval,
        }, ct).ConfigureAwait(false);

        var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path,
            Encoding.UTF8.GetBytes(options.Path))]);
        _session = await MoqSession.ConnectAsync(_connection, setup, ct).ConfigureAwait(false);

        MoqPublisher publisher = MoqPublisher.Create(_session);
        TrackNamespace @namespace = context.TrackNamespace;

        // Group ids have to be unique for the life of the track, which outlives this session: a
        // relay caches by group id and a restarted publisher that began again at 0 would be
        // republishing group 0 with different content. It resolves that collision by dropping the
        // subscriber — every viewer, until its cache expires. Wall-clock milliseconds is the
        // spec's own suggestion (MSF §6.1) and the only thing available that a restart cannot
        // repeat.
        var firstGroupId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _catalogTrack = new MoqCatalogTrack(publisher.PublishTrack(MoqCatalogTrack.NameIn(@namespace)), firstGroupId);
        _video = new MoqFrameTrack(publisher.PublishTrack(Track(context, "video0")), options.StreamMapping,
            firstGroupId);
        _audio = new MoqFrameTrack(publisher.PublishTrack(Track(context, "audio0")), options.StreamMapping,
            firstGroupId);

        await publisher.AnnounceNamespaceAsync(@namespace, ct).ConfigureAwait(false);
        s_logger.ZLogInformation(
            $"MOQT: announced '{options.Namespace}' to {options.Relay}; serving subscriptions.");

        _demux = publisher.RunAsync(_lifetime.Token);

        // Nothing is published until a subscriber asks, so "is anyone watching, and under what
        // alias" is the first question when a viewer sees nothing.
        LogFirstSubscriber("catalog", _catalogTrack.WaitForSubscriberAsync());
        LogFirstSubscriber(options.TrackNamePrefix + "video0", _video.WaitForSubscriberAsync());
        LogFirstSubscriber(options.TrackNamePrefix + "audio0", _audio.WaitForSubscriberAsync());
    }

    [SuppressMessage("Reliability", "CA2008:Do not create tasks without passing a TaskScheduler",
        Justification = "A continuation that writes one log line; the scheduler is immaterial.")]
    private static void LogFirstSubscriber(string track, Task<ulong> subscribed) =>
        _ = subscribed.ContinueWith(
            t => s_logger.ZLogInformation($"MOQT: '{track}' has a subscriber (alias {t.Result})."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private static FullTrackName Track(MoqSenderContext context, string name) =>
        FullTrackName.FromStrings([context.Options.Namespace], context.Options.TrackNamePrefix + name);

    private async Task ReadAsync(MoqSenderContext context, PipeReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAtLeastAsync(MediaFrameHeader.Size, ct).ConfigureAwait(false);
            if (result.Buffer.Length < MediaFrameHeader.Size)
            {
                break; // intake completed
            }

            ReadOnlySequence<byte> headerBuff = result.Buffer.Slice(0, MediaFrameHeader.Size);
            MediaFrameHeader header = BufferMarshal.AsRefOrCopy<MediaFrameHeader>(headerBuff);
            reader.AdvanceTo(headerBuff.End);

            if (header.Length <= 0)
            {
                continue;
            }

            result = await reader.ReadAtLeastAsync(header.Length, ct).ConfigureAwait(false);
            if (result.Buffer.Length < header.Length)
            {
                break; // intake completed halfway; drop the partial frame
            }

            ReadOnlySequence<byte> payload = result.Buffer.Slice(0, header.Length);
            _frame.ResetWrittenCount();
            payload.CopyTo(_frame.GetSpan(header.Length));
            _frame.Advance(header.Length);
            reader.AdvanceTo(payload.End);

            await ProcessFrameAsync(context, header, _frame.WrittenMemory, ct).ConfigureAwait(false);
        }
    }

    private ValueTask ProcessFrameAsync(MoqSenderContext context, in MediaFrameHeader header,
        ReadOnlyMemory<byte> frame, CancellationToken ct) => header.Kind switch
    {
        MediaFrameKind.Video => ProcessVideoAsync(context, header, frame, ct),
        MediaFrameKind.Audio => ProcessAudioAsync(context, header, frame, ct),
        // Timed metadata has no LOC mapping (it would be its own event-timeline track in MSF), so
        // it stops here rather than being mistaken for media.
        _ => ValueTask.CompletedTask,
    };

    private ValueTask ProcessVideoAsync(MoqSenderContext context, in MediaFrameHeader header,
        ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (header.IsConfig)
        {
            _videoCodec = header.VideoCodec;
            _videoConfig = frame.ToArray();
            RebuildCatalog(context);
            return ValueTask.CompletedTask;
        }

        if (_videoConfig is null)
        {
            if (!_warnedNoVideoConfig)
            {
                _warnedNoVideoConfig = true;
                s_logger.ZLogWarning($"MOQT: video frame arrived before the codec config; dropped.");
            }

            return ValueTask.CompletedTask;
        }

        if (!_video!.HasSubscriber)
        {
            _videoStarted = false; // whatever we send next must start a group a subscriber can decode
            return ValueTask.CompletedTask;
        }

        if (!_videoStarted)
        {
            if (!header.IsKeyFrame)
            {
                return ValueTask.CompletedTask; // a group cannot open on a frame that refers backwards
            }

            _videoStarted = true;
        }

        // The decoder configuration rides on every keyframe, which is what makes a group a place a
        // subscriber can join: it never has to have seen an earlier one.
        IReadOnlyList<MoqKeyValuePair> properties = VideoProperties(context, header, _videoConfig);
        return _video.PublishFrameAsync(frame, properties, startsGroup: header.IsKeyFrame,
            cancellationToken: ct);
    }

    private ValueTask ProcessAudioAsync(MoqSenderContext context, in MediaFrameHeader header,
        ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (header.IsConfig)
        {
            _audioCodec = header.AudioCodec;
            _audioConfig = frame.ToArray();
            RebuildCatalog(context);
            return ValueTask.CompletedTask;
        }

        if (_audioConfig is null || !_audio!.HasSubscriber)
        {
            return ValueTask.CompletedTask;
        }

        // Every audio frame decodes on its own, so every one opens a group: a subscriber can join at
        // any of them.
        return _audio.PublishFrameAsync(frame, AudioProperties(context, header), startsGroup: true,
            cancellationToken: ct);
    }

    // LOC's timestamp is the frame's presentation time. Spangle's canonical frame carries the
    // decode time in milliseconds plus the composition offset that turns it into one.
    private static ulong PresentationMicroseconds(in MediaFrameHeader header) =>
        (ulong)Math.Max(0, ((long)header.Timestamp + header.CompositionTimeMs) * 1000);

    private static IReadOnlyList<MoqKeyValuePair> VideoProperties(MoqSenderContext context,
        in MediaFrameHeader header, byte[] config)
    {
        ulong pts = PresentationMicroseconds(header);
        if (context.Options.Loc == LocDraft.Draft01)
        {
            // -01 words this field as wall-clock microseconds, but every implementation puts the
            // frame's own presentation time in it — moq-playa's broadcaster sends the WebCodecs
            // chunk timestamp, which is media time from the start of capture, and its player feeds
            // the value straight back to a decoder. Media time is what a player can use; wall clock
            // would only tell it when we encoded.
            return header.IsKeyFrame
                ? [Loc01Properties.CaptureTimestamp(pts), Loc01Properties.VideoConfig(config)]
                : [Loc01Properties.CaptureTimestamp(pts)];
        }

        return header.IsKeyFrame
            ? [.. Loc03Properties.MediaTime(pts), Loc03Properties.VideoConfig(config)]
            : Loc03Properties.MediaTime(pts);
    }

    private static IReadOnlyList<MoqKeyValuePair> AudioProperties(MoqSenderContext context,
        in MediaFrameHeader header)
    {
        ulong pts = PresentationMicroseconds(header);
        return context.Options.Loc == LocDraft.Draft01
            ? [Loc01Properties.CaptureTimestamp(pts)]
            : Loc03Properties.MediaTime(pts);
    }

    /// <summary>
    /// Rebuilds the catalog from the configs seen so far and starts publishing it if this is the
    /// first one. The catalog is what makes any of this reachable, and it can only be written once
    /// a config has said what the codec is — so the track list grows as the stream declares itself.
    /// </summary>
    private void RebuildCatalog(MoqSenderContext context)
    {
        var tracks = new List<MsfTrack>(2);
        if (_videoConfig is not null)
        {
            tracks.Add(VideoTrack(context, _videoConfig));
        }

        if (_audioConfig is not null)
        {
            tracks.Add(AudioTrack(context, _audioConfig));
        }

        if (tracks.Count == 0)
        {
            return;
        }

        _catalog = new MsfCatalog { Draft = context.Options.CatalogDraft, Tracks = tracks };
        _catalogLoop ??= Task.Run(() => PublishCatalogAsync(context, _lifetime!.Token), CancellationToken.None);
    }

    private MsfTrack VideoTrack(MoqSenderContext context, byte[] config)
    {
        (uint width, uint height) = Dimensions(context, config);
        return new MsfTrack
        {
            Name = context.Options.TrackNamePrefix + "video0",
            Packaging = MsfPackaging.Loc,
            IsLive = true,
            Role = MsfTrackRole.Video,
            RenderGroup = 1,
            Codec = _videoCodec switch
            {
                VideoCodec.H264 => CodecStrings.FromAvcC(config),
                VideoCodec.H265 => CodecStrings.FromHvcC(config),
                VideoCodec.AV1 => CodecStrings.FromAv1C(config),
                _ => null,
            },
            Width = width == 0 ? null : width,
            Height = height == 0 ? null : height,
            // LOC-01 states its timestamps in microseconds and has no timescale of its own, so the
            // catalog is where a subscriber is told the unit.
            Timescale = 1_000_000,
            // The keyframes carry this too (LOC's Video Config property), and a player is happy to
            // take it from either. Stating it here as well costs a few hundred bytes per catalog and
            // means a decoder can be built before the first keyframe arrives — the difference
            // between playing at the next keyframe and playing at the one after.
            InitData = context.Options.CatalogDraft == MsfDraft.Draft00 ? Convert.ToBase64String(config) : null,
        };
    }

    private (uint Width, uint Height) Dimensions(MoqSenderContext context, byte[] config)
    {
        // The receiver knows the dimensions when the container declared them; otherwise they are in
        // the config record itself, which is the only place an SRT/TS source ever says.
        if (context.SourceInfo is { VideoWidth: > 0, VideoHeight: > 0 } source)
        {
            return ((uint)source.VideoWidth, (uint)source.VideoHeight);
        }

        try
        {
            return _videoCodec switch
            {
                VideoCodec.H264 => Codecs.AVC.AvcSps.ParseDimensionsFromRecord(config),
                VideoCodec.H265 => Codecs.HEVC.HvcCBuilder.ParseDimensionsFromRecord(config),
                _ => (0, 0),
            };
        }
        catch (InvalidDataException e)
        {
            // A catalog without dimensions is still usable — a player sizes itself from the decoded
            // frames — so this is not worth failing the stream over.
            s_logger.ZLogWarning($"MOQT: could not read the video dimensions from the codec config: {e.Message}");
            return (0, 0);
        }
    }

    private MsfTrack AudioTrack(MoqSenderContext context, byte[] config)
    {
        uint sampleRate;
        uint channels;
        if (_audioCodec == AudioCodec.AAC)
        {
            AudioSpecificConfig asc = AudioSpecificConfig.Parse(config);
            sampleRate = (uint)asc.SampleRate;
            channels = asc.ChannelConfiguration;
        }
        else
        {
            OpusPacket.OpusHeadInfo head = OpusPacket.ParseOpusHead(config);
            sampleRate = OpusPacket.SampleRate; // an Opus track's clock is always 48 kHz
            channels = head.ChannelCount;
        }

        return new MsfTrack
        {
            Name = context.Options.TrackNamePrefix + "audio0",
            Packaging = MsfPackaging.Loc,
            IsLive = true,
            Role = MsfTrackRole.Audio,
            RenderGroup = 1,
            Codec = CodecStrings.FromAudio(_audioCodec, config),
            SampleRate = sampleRate,
            ChannelConfig = channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Timescale = 1_000_000,
            // LOC-01 has no audio config property, so unlike video — whose keyframes carry their own
            // avcC — the catalog is the only place an AAC decoder's description can come from.
            InitData = context.Options.CatalogDraft == MsfDraft.Draft00 ? Convert.ToBase64String(config) : null,
        };
    }

    private async Task PublishCatalogAsync(MoqSenderContext context, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_catalog is { } catalog)
                {
                    // Republished on a timer as well as on change: a subscriber that arrives between
                    // changes would otherwise wait for one to learn the stream exists at all.
                    await _catalogTrack!.PublishAsync(
                        catalog with { GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct)
                        .ConfigureAwait(false);
                }

                await Task.Delay(context.Options.CatalogInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception e)
        {
            // The catalog loop is the only thing on this task; if it dies the stream is
            // undiscoverable, but the media path is unaffected and the session should not fall over.
            s_logger.ZLogError($"MOQT: the catalog stopped being published: {e}");
        }
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Shutdown: a peer that has already gone is the ordinary case and must not mask the reason we are stopping.")]
    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null)
        {
            return; // never connected
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            // Close the open groups before the session goes: a subscriber that never hears a group
            // ended waits out a timeout on it.
            if (_video is not null)
            {
                await _video.DisposeAsync().ConfigureAwait(false);
            }

            if (_audio is not null)
            {
                await _audio.DisposeAsync().ConfigureAwait(false);
            }

            if (_catalogTrack is not null)
            {
                await _catalogTrack.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            s_logger.ZLogDebug($"MOQT: closing the tracks failed on shutdown: {e.Message}");
        }

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
                // both stop by cancellation, and either may report the session dying under it
            }
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
        _lifetime = null;
        _video = null;
        _audio = null;
        _catalogTrack = null;
        _session = null;
        _connection = null;
    }
}
