using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AAC;
using Spangle.Containers.ISOBMFF;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// HLS sender for CMAF/fMP4 segments. Consumes <see cref="MediaFrameHeader"/> records
/// directly from the intake pipe and muxes them itself (no spinner in between):
/// FLV codec payloads (length-prefixed NALUs / raw AAC) are already in the form
/// ISO-BMFF samples expect.
/// </summary>
public class CmafHLSSender : ISender<HLSSenderContext>, IDisposable
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
                var result = await reader.ReadAtLeastAsync(MediaFrameHeader.Size, ct);
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

                result = await reader.ReadAtLeastAsync(header.Length, ct);
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
            if (context.EndBehavior == HLSEndBehavior.Handover && context.Registry is { } registry
                && builder.ExportHandover() is { } handover)
            {
                // taken over: leave the playlist live for the successor session
                registry.StashHandover(context.ResolveStreamKey(), handover);
                s_logger.ZLogInformation($"HLS(CMAF) stream handed over");
            }
            else
            {
                builder.Complete();
                s_logger.ZLogInformation($"HLS(CMAF) stream completed");
            }
            await reader.CompleteAsync();
        }
    }
}

/// <summary>
/// Buffers media frames per fragment, cuts at keyframes once the target duration is
/// reached, and writes init.mp4 / .m4s segments plus the playlist.
/// In low-latency mode each segment is built from smaller partial fragments
/// (LL-HLS parts); the segment file is the concatenation of its parts.
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

    private string? _directory;
    private HLSPlaylist? _playlist;
    private CmafPackager? _packager;

    // Track configuration (arrives as Config frames before coded frames)
    private VideoCodec? _videoCodec;
    private byte[]? _videoConfig;
    private byte[]? _audioConfig;
    private AudioSpecificConfig _asc;

    // Samples of the fragment (= part in LL mode) being built:
    // raw bytes in reused buffers, positions and timing in metadata lists
    private readonly ArrayBufferWriter<byte> _videoData = new(64 * 1024);
    private readonly ArrayBufferWriter<byte> _audioData = new(16 * 1024);
    private readonly List<VideoSampleMeta> _videoMeta = new();
    private readonly List<AudioSampleMeta> _audioMeta = new();
    private uint _firstAudioTsMs;

    // The current segment accumulates here (one fragment per part); reused across segments
    private readonly MemoryStream _segmentStream = new();

    private bool _hasSegmentStart;
    private uint _segmentStartMs;
    private uint _partStartMs;
    private uint _lastVideoDurationMs = 33;

    private struct VideoSampleMeta
    {
        public int  Offset;
        public int  Length;
        public uint DtsMs;
        public int  CtsMs;
        public bool Sync;
    }

    private struct AudioSampleMeta
    {
        public int Offset;
        public int Length;
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
        if (header.AudioCodec != AudioCodec.AAC)
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {header.AudioCodec}");
            return;
        }

        if (header.IsConfig)
        {
            _audioConfig = payload.ToArray();
            _asc = AudioSpecificConfig.Parse(_audioConfig);
            s_logger.ZLogDebug(
                $"AudioSpecificConfig: AOT={_asc.AudioObjectType}, rate={_asc.SampleRate}, ch={_asc.ChannelConfiguration}");
            return;
        }

        if (_audioConfig is null)
        {
            s_logger.ZLogWarning($"AAC frame arrived before AudioSpecificConfig; dropped");
            return;
        }

        if (_audioMeta.Count == 0)
        {
            _firstAudioTsMs = header.Timestamp;
        }
        var length = (int)payload.Length;
        payload.CopyTo(_audioData.GetSpan(length));
        _audioMeta.Add(new AudioSampleMeta { Offset = _audioData.WrittenCount, Length = length });
        _audioData.Advance(length);
    }

    /// <summary>
    /// Builds one CMAF fragment (moof+mdat) from the buffered samples, ending at
    /// <paramref name="endTsMs"/>, and appends it to the current segment.
    /// In LL mode the fragment is also published as an EXT-X-PART.
    /// </summary>
    private void FlushPart(uint endTsMs)
    {
        if (_videoMeta.Count == 0 && _audioMeta.Count == 0)
        {
            return;
        }

        EnsureInitialized();

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

        var audioBuffer = _audioData.WrittenMemory;
        var audio = new CmafSample[_audioMeta.Count];
        for (var i = 0; i < _audioMeta.Count; i++)
        {
            AudioSampleMeta meta = _audioMeta[i];
            audio[i] = new CmafSample
            {
                Data = audioBuffer.Slice(meta.Offset, meta.Length),
                Duration = AacSamplesPerFrame,
                CompositionOffset = 0,
                IsSync = true,
            };
        }
        ulong audioBaseTime = _audioMeta.Count > 0
            ? (ulong)_firstAudioTsMs * _asc.SampleRate / 1000
            : 0;

        long fragmentStart = _segmentStream.Length;
        _packager!.BuildFragment(_partStartMs * 90ul, video, audioBaseTime, audio, _segmentStream);

        if (_partTarget is not null)
        {
            bool independent = _videoMeta.Count > 0 && _videoMeta[0].Sync;
            string partName = _playlist!.NextPartName();
            using (var file = File.Create(Path.Combine(_directory!, partName)))
            {
                file.Write(_segmentStream.GetBuffer(), (int)fragmentStart, (int)(_segmentStream.Length - fragmentStart));
            }
            _playlist.AddPart(partName, (endTsMs - _partStartMs) / 1000.0, independent);
        }

        _videoData.ResetWrittenCount();
        _audioData.ResetWrittenCount();
        _videoMeta.Clear();
        _audioMeta.Clear();
        _partStartMs = endTsMs;
    }

    /// <summary>Completes the current segment (= all its fragments) ending at <paramref name="endTsMs"/></summary>
    private void FinalizeSegment(uint endTsMs)
    {
        FlushPart(endTsMs);
        if (_segmentStream.Length == 0)
        {
            return;
        }

        string name = _playlist!.NextSegmentName(".m4s");
        using (var file = File.Create(Path.Combine(_directory!, name)))
        {
            file.Write(_segmentStream.GetBuffer(), 0, (int)_segmentStream.Length);
        }
        _playlist.AddSegment(name, (endTsMs - _segmentStartMs) / 1000.0);

        _segmentStream.SetLength(0);
        _segmentStartMs = endTsMs;
    }

    private void EnsureInitialized()
    {
        if (_packager is not null)
        {
            return;
        }

        _directory = context.ResolveStreamDirectory();
        Directory.CreateDirectory(_directory);

        var videoTrack = new CmafVideoTrack
        {
            Codec = _videoCodec!.Value,
            ConfigRecord = _videoConfig!,
            Width = context.SourceInfo?.VideoWidth ?? 0,
            Height = context.SourceInfo?.VideoHeight ?? 0,
        };
        CmafAudioTrack? audioTrack = _audioConfig is not null
            ? new CmafAudioTrack
            {
                AudioSpecificConfig = _audioConfig,
                SampleRate = _asc.SampleRate,
                ChannelCount = _asc.ChannelConfiguration,
            }
            : null;

        _packager = new CmafPackager(videoTrack, audioTrack);
        File.WriteAllBytes(Path.Combine(_directory, "init.mp4"), _packager.BuildInitSegment());

        Action<string, long, int>? onUpdated = null;
        if (context.Registry is { } registry)
        {
            var live = registry.GetOrAdd(context.ResolveStreamKey());
            onUpdated = live.Publish;
        }
        HLSPlaylistHandover? resume = context.Registry?.TakeHandover(context.ResolveStreamKey());
        _playlist = new HLSPlaylist(_directory, "init.mp4", _partTarget, onUpdated, resume);
        s_logger.ZLogInformation(
            $"HLS(CMAF) output to {_directory} (video={_videoCodec}, audio={(audioTrack is null ? "none" : "AAC")}, lowLatency={_partTarget is not null})");
    }

    /// <summary>Flushes the remaining samples and finalizes the playlist</summary>
    public void Complete()
    {
        FlushRemainder();
        _playlist?.Complete();
    }

    /// <summary>
    /// Flushes the remaining samples and exports the live playlist state for a
    /// successor session (takeover): no EXT-X-ENDLIST is written. Null when no
    /// output was produced yet.
    /// </summary>
    public HLSPlaylistHandover? ExportHandover()
    {
        FlushRemainder();
        return _playlist?.ExportHandover();
    }

    private void FlushRemainder()
    {
        if (_videoConfig is not null && _videoMeta.Count > 0)
        {
            FinalizeSegment(_videoMeta[^1].DtsMs + _lastVideoDurationMs);
        }
        else if (_segmentStream.Length > 0)
        {
            FinalizeSegment(_partStartMs);
        }
    }
}
