using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AAC;
using Spangle.Codecs.Opus;
using Spangle.Containers.ISOBMFF;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Spinner;
using Spangle.Transport.DASH;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// HLS sender for CMAF/fMP4 segments. Consumes <see cref="MediaFrameHeader"/> records
/// directly from the intake pipe and muxes them itself (no spinner in between):
/// FLV codec payloads (length-prefixed NALUs / raw AAC) are already in the form
/// ISO-BMFF samples expect.
/// </summary>
public sealed class CmafHLSSender : ISender<HLSSenderContext>, IDisposable
{
    private static readonly ILogger<CmafHLSSender> s_logger = SpangleLogManager.GetLogger<CmafHLSSender>();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async ValueTask StartAsync(HLSSenderContext context)
    {
        var ct = context.CancellationToken;
        var reader = context.IntakeReader;
        var builder = new CmafSegmentBuilder(context);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAtLeastAsync(MediaFrameHeader.Size, ct).ConfigureAwait(false);
                if (result.Buffer.Length < MediaFrameHeader.Size)
                {
                    break; // intake completed
                }
                var headerBuff = result.Buffer.Slice(0, MediaFrameHeader.Size);
                var header = BufferMarshal.AsRefOrCopy<MediaFrameHeader>(headerBuff);
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
                var payload = result.Buffer.Slice(0, header.Length);

                builder.ProcessFrame(in header, payload);

                reader.AdvanceTo(payload.End);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            if (context.EndBehavior == HLSEndBehavior.Handover && context.Registry is { } registry)
            {
                // taken over: leave the playlists live for the successor session
                foreach ((string key, HLSPlaylistHandover handover) in builder.ExportHandovers())
                {
                    registry.StashHandover(key, handover);
                }
                s_logger.ZLogInformation($"HLS(CMAF) stream handed over");
            }
            else
            {
                builder.Complete();
                if (context.Registry is { } reg)
                {
                    foreach (string key in builder.RegistryKeys)
                    {
                        reg.Remove(key);
                    }
                }
                s_logger.ZLogInformation($"HLS(CMAF) stream completed");
            }
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Buffers media frames per fragment, cuts at keyframes once the target duration is
/// reached, and writes demuxed per-track outputs: init_v.mp4 / init_a.mp4, aligned
/// segV%05d / segA%05d segment sequences, media playlists (video.m3u8 / audio.m3u8),
/// a multivariant playlist.m3u8, and one MPD over the same segments.
/// In low-latency mode each segment is built from smaller partial fragments
/// (LL-HLS parts) which also grow the in-progress segment blob (LL-DASH).
/// <para>
/// Sample bytes are accumulated in reused buffers (one per track) with small metadata
/// records, so steady-state operation performs no per-frame allocations.
/// </para>
/// </summary>
internal sealed class CmafSegmentBuilder(HLSSenderContext context)
{
    private static readonly ILogger<CmafSegmentBuilder> s_logger = SpangleLogManager.GetLogger<CmafSegmentBuilder>();

    private const uint AacSamplesPerFrame = 1024;

    private readonly double? _partTarget = context.LowLatency ? context.PartTargetDuration : null;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "the MemoryStream is a reusable growable buffer over managed memory; Dispose would release nothing")]
    private sealed class TrackOutput
    {
        public required CmafPackager Packager { get; init; }
        public required HLSPlaylist Playlist { get; init; }
        public required DashTrack Dash { get; init; }
        public required string RegistryKey { get; init; }
        public readonly MemoryStream SegmentStream = new();
        public bool Appended; // the LL live blob got this segment's fragments
    }

    private IHLSStreamStorage? _storage;
    private ILiveBlobStreamStorage? _liveBlobs;
    private TrackOutput? _videoOut;
    private TrackOutput? _audioOut;
    private DashManifest? _dash;
    private bool _initialized;

    // Track configuration (arrives as Config frames before coded frames)
    private VideoCodec? _videoCodec;
    private byte[]? _videoConfig;
    private AudioCodec? _audioCodec;
    private byte[]? _audioConfig;
    private uint _audioSampleRate;
    private ushort _audioChannels;

    // Samples of the fragment (= part in LL mode) being built:
    // raw bytes in reused buffers, positions and timing in metadata lists
    private readonly ArrayBufferWriter<byte> _videoData = new(64 * 1024);
    private readonly ArrayBufferWriter<byte> _audioData = new(16 * 1024);
    private readonly List<VideoSampleMeta> _videoMeta = new();
    private readonly List<AudioSampleMeta> _audioMeta = new();
    private uint _firstAudioTsMs;
    private uint _lastAudioTsMs;

    private bool _hasSegmentStart;
    private uint _segmentStartMs;
    private uint _partStartMs;
    private uint _lastVideoDurationMs = 33;
    private bool _forcedCutWarned;

    [StructLayout(LayoutKind.Auto)]
    private struct VideoSampleMeta
    {
        public int  Offset;
        public int  Length;
        public uint DtsMs;
        public int  CtsMs;
        public bool Sync;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct AudioSampleMeta
    {
        public int  Offset;
        public int  Length;
        public uint Duration; // in the audio timescale (sample rate)
    }

    // Timed-metadata events awaiting the next fragment (rare; one alloc per event)
    private readonly List<CmafEvent> _pendingEvents = new();

    public IEnumerable<string> RegistryKeys
    {
        get
        {
            if (_videoOut is not null)
            {
                yield return _videoOut.RegistryKey;
            }
            if (_audioOut is not null)
            {
                yield return _audioOut.RegistryKey;
            }
        }
    }

    public void ProcessFrame(in MediaFrameHeader header, ReadOnlySequence<byte> payload)
    {
        switch (header.Kind)
        {
            case MediaFrameKind.Video:
                ProcessVideoFrame(in header, payload);
                break;
            case MediaFrameKind.Audio:
                ProcessAudioFrame(in header, payload);
                break;
            case MediaFrameKind.Data:
                if ((DataCodec)header.Codec == DataCodec.Id3)
                {
                    _pendingEvents.Add(new CmafEvent { TimeMs = header.Timestamp, Id3 = payload.ToArray() });
                }
                else
                {
                    s_logger.ZLogTrace($"Dropping non-ID3 data frame: {(DataCodec)header.Codec}");
                }
                break;
            default:
                throw new InvalidDataException($"Unknown media frame kind: {header.Kind}");
        }
    }

    private void ProcessVideoFrame(in MediaFrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (header.IsConfig)
        {
            _videoCodec = header.VideoCodec;
            _videoConfig = payload.ToArray();
            s_logger.ZLogDebug($"Video config: codec={_videoCodec}, {_videoConfig.Length} bytes");
            return;
        }

        if (_videoConfig is null)
        {
            s_logger.ZLogWarning($"Video frame arrived before the codec config; dropped");
            return;
        }

        if (header.IsKeyFrame && _hasSegmentStart
            && (header.Timestamp - _segmentStartMs) / 1000.0 >= context.TargetSegmentDuration)
        {
            FinalizeSegment(header.Timestamp);
        }
        else if (_hasSegmentStart
                 && (header.Timestamp - _segmentStartMs) / 1000.0 >= context.TargetSegmentDuration * 4)
        {
            // No keyframe for 4x the target: cut anyway so the sample buffers stay
            // bounded (a source with a broken keyframe cadence would otherwise grow
            // one segment without limit)
            if (!_forcedCutWarned)
            {
                _forcedCutWarned = true;
                s_logger.ZLogWarning(
                    $"No keyframe for {context.TargetSegmentDuration * 4:F0}s; forcing a segment cut at a non-keyframe");
            }
            FinalizeSegment(header.Timestamp);
        }
        else if (_partTarget is { } partTarget && _hasSegmentStart && _videoMeta.Count > 0
                 && (header.Timestamp - _partStartMs) / 1000.0 >= partTarget)
        {
            FlushPart(header.Timestamp);
        }

        if (!_hasSegmentStart)
        {
            _hasSegmentStart = true;
            _segmentStartMs = header.Timestamp;
            _partStartMs = header.Timestamp;
        }

        var length = (int)payload.Length;
        payload.CopyTo(_videoData.GetSpan(length));
        _videoMeta.Add(new VideoSampleMeta
        {
            Offset = _videoData.WrittenCount,
            Length = length,
            DtsMs = header.Timestamp,
            CtsMs = header.CompositionTimeMs,
            Sync = header.IsKeyFrame,
        });
        _videoData.Advance(length);
    }

    private void ProcessAudioFrame(in MediaFrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (header.AudioCodec is not (AudioCodec.AAC or AudioCodec.Opus))
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {header.AudioCodec}");
            return;
        }

        if (header.IsConfig)
        {
            _audioCodec = header.AudioCodec;
            _audioConfig = payload.ToArray();
            if (_audioCodec == AudioCodec.AAC)
            {
                var asc = AudioSpecificConfig.Parse(_audioConfig);
                _audioSampleRate = asc.SampleRate;
                _audioChannels = asc.ChannelConfiguration;
                s_logger.ZLogDebug(
                    $"AudioSpecificConfig: AOT={asc.AudioObjectType}, rate={asc.SampleRate}, ch={asc.ChannelConfiguration}");
            }
            else
            {
                var head = OpusPacket.ParseOpusHead(_audioConfig);
                _audioSampleRate = OpusPacket.SampleRate; // the Opus track clock is always 48 kHz
                _audioChannels = head.ChannelCount;
                s_logger.ZLogDebug(
                    $"OpusHead: ch={head.ChannelCount}, preSkip={head.PreSkip}, inputRate={head.InputSampleRate}");
            }
            return;
        }

        if (_audioConfig is null)
        {
            s_logger.ZLogWarning($"Audio frame arrived before the codec config; dropped");
            return;
        }

        // With no video track there are no keyframes; every AAC frame is a sync point,
        // so the audio timeline drives the segment and part cuts instead.
        if (context.SourceInfo?.IsAudioOnly == true)
        {
            if (_hasSegmentStart
                && (header.Timestamp - _segmentStartMs) / 1000.0 >= context.TargetSegmentDuration)
            {
                FinalizeSegment(header.Timestamp);
            }
            else if (_partTarget is { } partTarget && _hasSegmentStart && _audioMeta.Count > 0
                     && (header.Timestamp - _partStartMs) / 1000.0 >= partTarget)
            {
                FlushPart(header.Timestamp);
            }

            if (!_hasSegmentStart)
            {
                _hasSegmentStart = true;
                _segmentStartMs = header.Timestamp;
                _partStartMs = header.Timestamp;
            }
        }

        if (_audioMeta.Count == 0)
        {
            _firstAudioTsMs = header.Timestamp;
        }
        _lastAudioTsMs = header.Timestamp;
        var length = (int)payload.Length;
        Span<byte> dest = _audioData.GetSpan(length);
        payload.CopyTo(dest);
        // AAC frames are a fixed 1024 samples; Opus packets declare theirs in the TOC
        uint duration = _audioCodec == AudioCodec.Opus
            ? OpusPacket.GetSampleCount(dest[..length])
            : AacSamplesPerFrame;
        _audioMeta.Add(new AudioSampleMeta
        {
            Offset = _audioData.WrittenCount, Length = length, Duration = duration,
        });
        _audioData.Advance(length);
    }

    /// <summary>
    /// Builds one CMAF fragment per track (moof+mdat) from the buffered samples,
    /// ending at <paramref name="endTsMs"/>, and appends them to the current
    /// segments. In LL mode each fragment is also published as an EXT-X-PART and
    /// grows the in-progress segment blob (LL-DASH chunked delivery).
    /// </summary>
    private void FlushPart(uint endTsMs)
    {
        if (_videoMeta.Count == 0 && _audioMeta.Count == 0)
        {
            return;
        }

        EnsureInitialized();

        if (_videoOut is not null)
        {
            var videoBuffer = _videoData.WrittenMemory;
            var video = new CmafSample[_videoMeta.Count];
            for (var i = 0; i < _videoMeta.Count; i++)
            {
                VideoSampleMeta meta = _videoMeta[i];
                uint nextDtsMs = i + 1 < _videoMeta.Count ? _videoMeta[i + 1].DtsMs : endTsMs;
                uint durationMs = nextDtsMs > meta.DtsMs ? nextDtsMs - meta.DtsMs : _lastVideoDurationMs;
                _lastVideoDurationMs = durationMs;
                video[i] = new CmafSample
                {
                    Data = videoBuffer.Slice(meta.Offset, meta.Length),
                    Duration = durationMs * 90,
                    CompositionOffset = meta.CtsMs * 90,
                    IsSync = meta.Sync,
                };
            }

            // timed metadata rides on the video track's fragments (players read
            // emsg regardless of which SourceBuffer carried it)
            CmafEvent[]? events = TakePendingEvents();
            bool independent = _videoMeta.Count > 0 && _videoMeta[0].Sync;
            EmitFragment(_videoOut, _partStartMs * 90ul, video, isAudio: false, events, endTsMs, independent);
        }

        if (_audioOut is not null)
        {
            var audioBuffer = _audioData.WrittenMemory;
            var audio = new CmafSample[_audioMeta.Count];
            for (var i = 0; i < _audioMeta.Count; i++)
            {
                AudioSampleMeta meta = _audioMeta[i];
                audio[i] = new CmafSample
                {
                    Data = audioBuffer.Slice(meta.Offset, meta.Length),
                    Duration = meta.Duration,
                    CompositionOffset = 0,
                    IsSync = true,
                };
            }
            ulong audioBaseTime = _audioMeta.Count > 0
                ? (ulong)_firstAudioTsMs * _audioSampleRate / 1000
                : 0;

            CmafEvent[]? events = _videoOut is null ? TakePendingEvents() : null;
            EmitFragment(_audioOut, audioBaseTime, audio, isAudio: true, events, endTsMs,
                independent: _audioMeta.Count > 0);
        }

        _videoData.ResetWrittenCount();
        _audioData.ResetWrittenCount();
        _videoMeta.Clear();
        _audioMeta.Clear();
        _partStartMs = endTsMs;
    }

    private CmafEvent[]? TakePendingEvents()
    {
        if (_pendingEvents.Count == 0)
        {
            return null;
        }
        CmafEvent[] events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return events;
    }

    private void EmitFragment(TrackOutput track, ulong baseTime, CmafSample[] samples, bool isAudio,
        CmafEvent[]? events, uint endTsMs, bool independent)
    {
        long fragmentStart = track.SegmentStream.Length;
        if (isAudio)
        {
            track.Packager.BuildFragment(0, [], baseTime, samples, track.SegmentStream, events);
        }
        else
        {
            track.Packager.BuildFragment(baseTime, samples, 0, [], track.SegmentStream, events);
        }

        if (_partTarget is not null)
        {
            ReadOnlySpan<byte> fragment = track.SegmentStream.GetBuffer()
                .AsSpan((int)fragmentStart, (int)(track.SegmentStream.Length - fragmentStart));

            // LL-DASH: the fragment also grows the in-progress segment blob, which
            // the HTTP layer serves over chunked transfer before it completes
            if (_liveBlobs is not null)
            {
                _liveBlobs.AppendBlob(track.Playlist.NextSegmentName(".m4s"), fragment);
                track.Appended = true;
            }

            string partName = track.Playlist.NextPartName();
            _storage!.WriteBlob(partName, fragment);
            track.Playlist.AddPart(partName, (endTsMs - _partStartMs) / 1000.0, independent);
        }
    }

    /// <summary>Completes the current segments (= all their fragments) ending at <paramref name="endTsMs"/></summary>
    private void FinalizeSegment(uint endTsMs)
    {
        FlushPart(endTsMs);

        double duration = (endTsMs - _segmentStartMs) / 1000.0;
        FinalizeTrack(_audioOut, duration, endTsMs);
        // the driving playlist publishes last so the MPD sees both tracks' segments
        FinalizeTrack(_videoOut, duration, endTsMs);
        _segmentStartMs = endTsMs;
    }

    private void FinalizeTrack(TrackOutput? track, double duration, uint endTsMs)
    {
        if (track is null || track.SegmentStream.Length == 0)
        {
            return;
        }

        string name = track.Playlist.NextSegmentName(".m4s");
        if (track.Appended)
        {
            // the fragments were already appended part by part; seal the blob
            _liveBlobs!.CompleteBlob(name);
            track.Appended = false;
        }
        else
        {
            _storage!.WriteBlob(name, track.SegmentStream.GetBuffer().AsSpan(0, (int)track.SegmentStream.Length));
        }
        if (duration > 0.1)
        {
            track.Dash.Bandwidth = (long)(track.SegmentStream.Length * 8 / duration);
        }
        track.Playlist.AddSegment(name, duration);
        track.SegmentStream.SetLength(0);

        if (track == _videoOut || _videoOut is null)
        {
            PublishMultivariant();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        _storage = context.ResolveStreamStorage();
        // LL-DASH needs blobs readable while they grow; only the memory backend
        // provides that (by design — file-only low latency is out of spec)
        _liveBlobs = _partTarget is not null ? _storage as ILiveBlobStreamStorage : null;

        // MSE rejects a 0x0 coded size, so when the source provided no dimension
        // metadata, read them from the parameter sets inside the config record
        uint width = context.SourceInfo?.VideoWidth ?? 0;
        uint height = context.SourceInfo?.VideoHeight ?? 0;
        if (_videoConfig is not null && width == 0)
        {
            try
            {
                (width, height) = _videoCodec switch
                {
                    VideoCodec.H264 => Codecs.AVC.AvcSps.ParseDimensionsFromRecord(_videoConfig),
                    VideoCodec.H265 => Codecs.HEVC.HvcCBuilder.ParseDimensionsFromRecord(_videoConfig),
                    _ => (0u, 0u),
                };
            }
            catch (InvalidDataException e)
            {
                s_logger.ZLogWarning($"Could not derive video dimensions from the config record: {e.Message}");
            }
        }

        string streamKey = context.ResolveStreamKey();
        _dash = new DashManifest(_storage)
        {
            PartTargetDuration = _liveBlobs is not null ? _partTarget : null,
            TargetSegmentDuration = context.TargetSegmentDuration,
        };

        if (_videoConfig is not null)
        {
            var videoTrack = new CmafVideoTrack
            {
                Codec = _videoCodec!.Value,
                ConfigRecord = _videoConfig,
                Width = width,
                Height = height,
            };
            var packager = new CmafPackager(videoTrack, audio: null);
            _storage.WriteBlob("init_v.mp4", packager.BuildInitSegment());
            var dashTrack = new DashTrack
            {
                MimeType = "video/mp4",
                Codecs = _videoCodec switch
                {
                    VideoCodec.H264 => CodecStrings.FromAvcC(_videoConfig),
                    VideoCodec.H265 => CodecStrings.FromHvcC(_videoConfig),
                    VideoCodec.AV1 => CodecStrings.FromAv1C(_videoConfig),
                    _ => "unknown",
                },
                InitName = "init_v.mp4",
                SegmentPrefix = "segV",
                Width = width,
                Height = height,
            };
            _dash.Tracks.Add(dashTrack);
            _videoOut = CreateTrackOutput(streamKey, "video.m3u8", "segV", "init_v.mp4", packager, dashTrack,
                attachDash: true);
        }

        if (_audioConfig is not null)
        {
            var audioTrack = new CmafAudioTrack
            {
                Codec = _audioCodec!.Value,
                Config = _audioConfig,
                SampleRate = _audioSampleRate,
                ChannelCount = _audioChannels,
            };
            var packager = new CmafPackager(video: null, audioTrack);
            _storage.WriteBlob("init_a.mp4", packager.BuildInitSegment());
            var dashTrack = new DashTrack
            {
                MimeType = "audio/mp4",
                Codecs = CodecStrings.FromAudio(_audioCodec!.Value, _audioConfig) ?? "unknown",
                InitName = "init_a.mp4",
                SegmentPrefix = "segA",
            };
            _dash.Tracks.Add(dashTrack);
            _audioOut = CreateTrackOutput(streamKey, "audio.m3u8", "segA", "init_a.mp4", packager, dashTrack,
                attachDash: _videoConfig is null);
        }

        PublishMultivariant();
        s_logger.ZLogInformation(
            $"HLS(CMAF) output for {streamKey} to {context.StorageDescription} (video={(_videoOut is null ? "none" : _videoCodec!.Value.ToString())}, audio={(_audioOut is null ? "none" : _audioCodec!.Value.ToString())}, lowLatency={_partTarget is not null})");
    }

    private TrackOutput CreateTrackOutput(string streamKey, string playlistName, string segmentPrefix,
        string initName, CmafPackager packager, DashTrack dashTrack, bool attachDash)
    {
        string registryKey = $"{streamKey}/{playlistName}";
        Action<string, string?, long, int>? onUpdated = null;
        if (context.Registry is { } registry)
        {
            var live = registry.GetOrAdd(registryKey);
            onUpdated = live.Publish;
        }
        HLSPlaylistHandover? resume = context.Registry?.TakeHandover(registryKey);
        var playlist = new HLSPlaylist(_storage!, initName, _partTarget, onUpdated, resume,
            context.PlaylistWindow, attachDash ? _dash : null)
        {
            SegmentNamePrefix = segmentPrefix,
            PlaylistName = playlistName,
        };
        return new TrackOutput
        {
            Packager = packager,
            Playlist = playlist,
            Dash = dashTrack,
            RegistryKey = registryKey,
        };
    }

    /// <summary>
    /// The multivariant playlist: one variant referencing the video media playlist
    /// with the audio rendition group, or the audio playlist directly when no video
    /// exists. Re-published per segment so BANDWIDTH tracks the measured rate.
    /// </summary>
    private void PublishMultivariant()
    {
        if (_storage is null)
        {
            return;
        }

        var sb = new StringBuilder(256);
        sb.Append("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (_videoOut is not null)
        {
            if (_audioOut is not null)
            {
                sb.Append("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Audio\",DEFAULT=YES,");
                sb.Append("AUTOSELECT=YES,URI=\"audio.m3u8\"\n");
            }
            long bandwidth = _videoOut.Dash.Bandwidth + (_audioOut?.Dash.Bandwidth ?? 0);
            sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},CODECS=\"{_videoOut.Dash.Codecs}");
            if (_audioOut is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $",{_audioOut.Dash.Codecs}");
            }
            sb.Append('"');
            if (_videoOut.Dash.Width > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $",RESOLUTION={_videoOut.Dash.Width}x{_videoOut.Dash.Height}");
            }
            if (_audioOut is not null)
            {
                sb.Append(",AUDIO=\"audio\"");
            }
            sb.Append("\nvideo.m3u8\n");
        }
        else if (_audioOut is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={_audioOut.Dash.Bandwidth},CODECS=\"{_audioOut.Dash.Codecs}\"\n");
            sb.Append("audio.m3u8\n");
        }

        _storage.PublishPlaylist(sb.ToString());
    }

    /// <summary>Flushes the remaining samples and finalizes the playlists</summary>
    public void Complete()
    {
        FlushRemainder();
        _videoOut?.Playlist.Complete();
        _audioOut?.Playlist.Complete();
    }

    /// <summary>
    /// Flushes the remaining samples and exports the live playlist states for a
    /// successor session (takeover): no EXT-X-ENDLIST is written. Empty when no
    /// output was produced yet.
    /// </summary>
    public IReadOnlyList<(string RegistryKey, HLSPlaylistHandover Handover)> ExportHandovers()
    {
        FlushRemainder();
        var handovers = new List<(string, HLSPlaylistHandover)>(2);
        if (_videoOut is not null)
        {
            handovers.Add((_videoOut.RegistryKey, _videoOut.Playlist.ExportHandover()));
        }
        if (_audioOut is not null)
        {
            handovers.Add((_audioOut.RegistryKey, _audioOut.Playlist.ExportHandover()));
        }
        return handovers;
    }

    private void FlushRemainder()
    {
        if (_videoConfig is not null && _videoMeta.Count > 0)
        {
            FinalizeSegment(_videoMeta[^1].DtsMs + _lastVideoDurationMs);
        }
        else if (_videoConfig is null && _audioConfig is not null && _audioMeta.Count > 0)
        {
            FinalizeSegment(_lastAudioTsMs + _audioMeta[^1].Duration * 1000 / _audioSampleRate);
        }
        else if ((_videoOut?.SegmentStream.Length ?? 0) > 0 || (_audioOut?.SegmentStream.Length ?? 0) > 0)
        {
            FinalizeSegment(_partStartMs);
        }
    }
}
