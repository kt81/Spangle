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
            builder.Complete();
            await reader.CompleteAsync();
            s_logger.ZLogInformation($"HLS(CMAF) stream completed");
        }
    }
}

/// <summary>
/// Buffers media frames per fragment, cuts at keyframes once the target duration is
/// reached, and writes init.mp4 / .m4s segments plus the playlist.
/// </summary>
internal sealed class CmafSegmentBuilder(HLSSenderContext context)
{
    private static readonly ILogger<CmafSegmentBuilder> s_logger = SpangleLogManager.GetLogger<CmafSegmentBuilder>();

    private const uint AacSamplesPerFrame = 1024;

    private string? _directory;
    private HLSPlaylist? _playlist;
    private CmafPackager? _packager;

    // Track configuration (arrives as Config frames before coded frames)
    private VideoCodec? _videoCodec;
    private byte[]? _videoConfig;
    private byte[]? _audioConfig;
    private AudioSpecificConfig _asc;

    // Samples of the fragment being built
    private readonly List<(byte[] Data, uint DtsMs, int CtsMs, bool Sync)> _videoSamples = new();
    private readonly List<byte[]> _audioSamples = new();
    private uint _firstAudioTsMs;

    private bool _hasSegmentStart;
    private uint _segmentStartMs;
    private uint _lastVideoDurationMs = 33;

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

        if (header.IsKeyFrame && _hasSegmentStart && _videoSamples.Count > 0
            && (header.Timestamp - _segmentStartMs) / 1000.0 >= context.TargetSegmentDuration)
        {
            FinalizeFragment(header.Timestamp);
        }

        if (!_hasSegmentStart)
        {
            _hasSegmentStart = true;
            _segmentStartMs = header.Timestamp;
        }

        _videoSamples.Add((payload.ToArray(), header.Timestamp, header.CompositionTimeMs, header.IsKeyFrame));
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

        if (_audioSamples.Count == 0)
        {
            _firstAudioTsMs = header.Timestamp;
        }
        _audioSamples.Add(payload.ToArray());
    }

    /// <summary>Flushes the buffered samples as one media segment ending at <paramref name="endTsMs"/></summary>
    private void FinalizeFragment(uint endTsMs)
    {
        EnsureInitialized();

        var video = new CmafSample[_videoSamples.Count];
        for (var i = 0; i < _videoSamples.Count; i++)
        {
            (byte[] data, uint dtsMs, int ctsMs, bool sync) = _videoSamples[i];
            uint nextDtsMs = i + 1 < _videoSamples.Count ? _videoSamples[i + 1].DtsMs : endTsMs;
            uint durationMs = nextDtsMs > dtsMs ? nextDtsMs - dtsMs : _lastVideoDurationMs;
            _lastVideoDurationMs = durationMs;
            video[i] = new CmafSample
            {
                Data = data,
                Duration = durationMs * 90,
                CompositionOffset = ctsMs * 90,
                IsSync = sync,
            };
        }

        var audio = new CmafSample[_audioSamples.Count];
        for (var i = 0; i < _audioSamples.Count; i++)
        {
            audio[i] = new CmafSample
            {
                Data = _audioSamples[i],
                Duration = AacSamplesPerFrame,
                CompositionOffset = 0,
                IsSync = true,
            };
        }
        ulong audioBaseTime = _audioSamples.Count > 0
            ? (ulong)_firstAudioTsMs * _asc.SampleRate / 1000
            : 0;

        byte[] fragment = _packager!.BuildFragment(
            _segmentStartMs * 90ul, video, audioBaseTime, audio);

        string name = _playlist!.NextSegmentName(".m4s");
        File.WriteAllBytes(Path.Combine(_directory!, name), fragment);
        _playlist.AddSegment(name, (endTsMs - _segmentStartMs) / 1000.0);

        _videoSamples.Clear();
        _audioSamples.Clear();
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
        _playlist = new HLSPlaylist(_directory, "init.mp4");
        s_logger.ZLogInformation($"HLS(CMAF) output to {_directory} (video={_videoCodec}, audio={(audioTrack is null ? "none" : "AAC")})");
    }

    /// <summary>Flushes the remaining samples and finalizes the playlist</summary>
    public void Complete()
    {
        if (_videoConfig is not null && _videoSamples.Count > 0)
        {
            uint endTsMs = _videoSamples[^1].DtsMs + _lastVideoDurationMs;
            FinalizeFragment(endTsMs);
        }
        _playlist?.Complete();
    }
}
