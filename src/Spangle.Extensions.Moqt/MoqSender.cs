using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AAC;
using Spangle.Codecs.Opus;
using Spangle.Containers.ISOBMFF;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Net.Moqt.Wire;
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
/// The relay connection lives in <see cref="MoqRelayConnection"/>, one object per dial: codec
/// configs and the catalog built from them are session state and stay here, so a relay that was
/// down when the stream began (or died and came back) still gets the catalog — the configs arrive
/// once, at the front of the stream, and are captured whether or not anyone is reachable.
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

    private MoqRelayConnection? _relay;

    private byte[]? _videoConfig;
    private byte[]? _audioConfig;
    private VideoCodec _videoCodec;
    private AudioCodec _audioCodec;
    private volatile MsfCatalog? _catalog;
    private bool _videoStarted;
    private bool _warnedNoVideoConfig;
    private long _nextConnectAttempt; // Environment.TickCount64 before which no dial is attempted

    /// <inheritdoc />
    public async ValueTask StartAsync(MoqSenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken ct = context.CancellationToken;
        PipeReader reader = context.IntakeReader;

        try
        {
            await ReadAsync(context, reader, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the normal shutdown path
        }
        catch (Exception e)
        {
            // The host only sees this task's failure if it goes looking; this line is the
            // guaranteed trace that the egress died while the stream lived on.
            s_logger.ZLogError(e, $"MOQT: egress stopped unexpectedly; the session continues without it.");
            throw;
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

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

            // A codec config is session state, not media: capture it before any connection gate,
            // or a relay that is down right now would cost us a frame that arrives once, at the
            // front of the stream — and the egress would stay silent long after the relay
            // returned, with no config to package frames under and no catalog to announce.
            if (header.IsConfig && header.Kind is MediaFrameKind.Video or MediaFrameKind.Audio)
            {
                CaptureConfig(context, header, _frame.WrittenMemory);
                continue;
            }

            // The demux loop ends only when the connection under it has: this is how a dead relay
            // announces itself. Tear the connection down so the dial-in below rebuilds it — the
            // alternative is what it replaced: warning once per keyframe at a corpse, forever.
            if (_relay is { IsDead: true })
            {
                await TeardownConnectionAsync(context, "the session ended under us").ConfigureAwait(false);
            }

            // Connect on the first frame, not at startup: the namespace may be derived from the
            // stream key, and the key is only known once the publish command has been handled —
            // which the first MediaFrame's arrival proves. The same gate is the reconnect path.
            if (_relay is not { } relay)
            {
                if (Environment.TickCount64 < _nextConnectAttempt)
                {
                    continue; // backing off; the frame is dropped, as every frame of the gap is
                }

                try
                {
                    relay = await MoqRelayConnection.ConnectAsync(context, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // ConnectAsync released whatever it had built; nothing here to unwind.
                    _nextConnectAttempt = Environment.TickCount64
                                          + (long)context.Options.ReconnectDelay.TotalMilliseconds;
                    s_logger.ZLogWarning(
                        $"MOQT: could not reach the relay ({e.Message}); retrying in {context.Options.ReconnectDelay.TotalSeconds:0}s.");
                    continue;
                }

                MoqCatalogTrack catalogTrack = relay.CatalogTrack;
                relay.BeginPublishingCatalog(token => PublishCatalogAsync(context, catalogTrack, token));
                _relay = relay;
            }

            await ProcessFrameAsync(context, relay, header, _frame.WrittenMemory, ct).ConfigureAwait(false);
        }
    }

    private void CaptureConfig(MoqSenderContext context, in MediaFrameHeader header, ReadOnlyMemory<byte> frame)
    {
        switch (header.Kind)
        {
            case MediaFrameKind.Video:
                _videoCodec = header.VideoCodec;
                _videoConfig = frame.ToArray();
                break;
            case MediaFrameKind.Audio:
                _audioCodec = header.AudioCodec;
                _audioConfig = frame.ToArray();
                break;
            default:
                return;
        }

        RebuildCatalog(context);
    }

    private ValueTask ProcessFrameAsync(MoqSenderContext context, MoqRelayConnection relay,
        in MediaFrameHeader header, ReadOnlyMemory<byte> frame, CancellationToken ct) => header.Kind switch
    {
        MediaFrameKind.Video => ProcessVideoAsync(context, relay, header, frame, ct),
        MediaFrameKind.Audio => ProcessAudioAsync(context, relay, header, frame, ct),
        // Timed metadata has no LOC mapping (it would be its own event-timeline track in MSF), so
        // it stops here rather than being mistaken for media.
        _ => ValueTask.CompletedTask,
    };

    private async ValueTask ProcessVideoAsync(MoqSenderContext context, MoqRelayConnection relay,
        MediaFrameHeader header, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (_videoConfig is null)
        {
            if (!_warnedNoVideoConfig)
            {
                _warnedNoVideoConfig = true;
                s_logger.ZLogWarning($"MOQT: video frame arrived before the codec config; dropped.");
            }

            return;
        }

        if (!relay.Video.HasSubscriber)
        {
            _videoStarted = false; // whatever we send next must start a group a subscriber can decode
            return;
        }

        if (!_videoStarted)
        {
            if (!header.IsKeyFrame)
            {
                return; // a group cannot open on a frame that refers backwards
            }

            _videoStarted = true;
        }

        // The decoder configuration rides on every keyframe, which is what makes a group a place a
        // subscriber can join: it never has to have seen an earlier one.
        IReadOnlyList<MoqKeyValuePair> properties = VideoProperties(context, header, _videoConfig);
        try
        {
            await relay.Video.PublishFrameAsync(frame, properties, startsGroup: header.IsKeyFrame,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // A peer may reset any data stream — draft-18 makes the stream, not the session, the
            // unit of delivery, and a relay resets streams it no longer wants. That costs the group
            // the stream was carrying, nothing more; treating it as fatal is how one reset stream
            // becomes a dead broadcast. Resume at the next keyframe, where a group may begin.
            s_logger.ZLogWarning($"MOQT: the video group's stream was reset ({e.Message}); resuming at the next keyframe.");
            await relay.Video.AbandonGroupAsync().ConfigureAwait(false);
            _videoStarted = false;
        }
    }

    private async ValueTask ProcessAudioAsync(MoqSenderContext context, MoqRelayConnection relay,
        MediaFrameHeader header, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (_audioConfig is null || !relay.Audio.HasSubscriber)
        {
            return;
        }

        try
        {
            // Every audio frame decodes on its own, so every one opens a group: a subscriber can
            // join at any of them.
            await relay.Audio.PublishFrameAsync(frame, AudioProperties(context, header), startsGroup: true,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Same reasoning as the video path: a reset stream costs its group, not the track —
            // and an audio group is one frame, so it costs almost nothing.
            s_logger.ZLogWarning($"MOQT: an audio group's stream was reset ({e.Message}); continuing.");
            await relay.Audio.AbandonGroupAsync().ConfigureAwait(false);
        }
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
    /// Rebuilds the catalog from the configs seen so far. The catalog is what makes any of this
    /// reachable, and it can only be written once a config has said what the codec is — so the
    /// track list grows as the stream declares itself, whether or not a relay is connected yet.
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
        // One line per rebuild — a config arriving is what grows the track list, so this is the
        // fastest way to see which configs did. The catalog loop itself is per-connection and
        // starts when a connection comes up; this only refreshes what it publishes.
        s_logger.ZLogInformation($"MOQT: catalog now lists [{string.Join(", ", tracks.Select(t => $"{t.Name}:{t.Codec}"))}].");
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

    private async Task PublishCatalogAsync(MoqSenderContext context, MoqCatalogTrack catalogTrack,
        CancellationToken ct)
    {
        try
        {
            // Wait for the subscriber before reading the catalog, not after. PublishAsync blocks
            // until one arrives, and a catalog picked before that wait is stale by the time it is
            // sent: the audio config lands milliseconds after the video's, and a player selects its
            // tracks from the first catalog it sees — so shipping the pre-wait snapshot means every
            // A/V stream advertises itself as video-only to the subscriber that matters most.
#pragma warning disable VSTHRD003 // our own track's TCS, completed by our own demux loop
            await catalogTrack.WaitForSubscriberAsync().WaitAsync(ct).ConfigureAwait(false);
#pragma warning restore VSTHRD003

            while (!ct.IsCancellationRequested)
            {
                if (_catalog is { } catalog)
                {
                    try
                    {
                        // Republished on a timer as well as on change: a subscriber that arrives
                        // between changes would otherwise wait for one to learn the stream exists.
                        await catalogTrack.PublishAsync(
                            catalog with { GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        // A reset stream costs this publication, not the loop: the catalog that
                        // failed to go out goes out again on the next tick.
                        s_logger.ZLogWarning($"MOQT: a catalog publication failed ({e.Message}); retrying on the next tick.");
                        await catalogTrack.AbandonGroupAsync().ConfigureAwait(false);
                    }
                }

                await Task.Delay(context.Options.CatalogInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Discards a connection that is already gone so the read loop can dial a new one, and
    /// remembers not to redial immediately. The unwinding itself lives with the connection
    /// (<see cref="MoqRelayConnection.AbandonAsync"/>).
    /// </summary>
    private async ValueTask TeardownConnectionAsync(MoqSenderContext context, string reason)
    {
        s_logger.ZLogWarning(
            $"MOQT: the relay connection was lost ({reason}); reconnecting in {context.Options.ReconnectDelay.TotalSeconds:0}s.");
        _nextConnectAttempt = Environment.TickCount64 + (long)context.Options.ReconnectDelay.TotalMilliseconds;

        if (_relay is { } relay)
        {
            _relay = null;
            await relay.AbandonAsync().ConfigureAwait(false);
        }

        _videoStarted = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_relay is { } relay)
        {
            _relay = null;
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }
}
