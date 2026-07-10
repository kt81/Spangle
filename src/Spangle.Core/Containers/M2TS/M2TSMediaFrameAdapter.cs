using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Spangle.Codecs;
using Spangle.Codecs.AAC;
using Spangle.Codecs.HEVC;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Containers.M2TS;

/// <summary>
/// Normalizes demultiplexed TS elementary streams into the application's canonical
/// <see cref="MediaFrameHeader"/> frames — the same shape the RTMP receiver emits —
/// so every downstream stage (TS re-mux, CMAF, spinners) works unchanged:
/// <list type="bullet">
/// <item>H.264 Annex B access units → length-prefixed samples + an avcC Config frame
/// built from the in-band SPS/PPS</item>
/// <item>ADTS AAC → raw AAC frames + an AudioSpecificConfig Config frame</item>
/// <item>90 kHz PES timestamps (33-bit, wrapping) → unwrapped milliseconds</item>
/// </list>
/// </summary>
internal sealed class M2TSMediaFrameAdapter<TContext>(ReceiverContextBase<TContext> context) : IM2TSDemuxerSink
    where TContext : ReceiverContextBase<TContext>
{
    private static readonly ILogger s_logger =
        SpangleLogManager.GetLogger<M2TSMediaFrameAdapter<TContext>>();

    private const ulong PtsMask = (1UL << 33) - 1;

    // ---- video state (H.264: SPS/PPS; H.265: VPS/SPS/PPS) ----
    private byte[]? _vps;
    private byte[]? _sps;
    private byte[]? _pps;
    private bool    _videoConfigSent;
    private readonly ArrayBufferWriter<byte> _sample = new(16 * 1024);
    private TimestampUnwrapper _videoTs;

    // ---- audio (AAC) state ----
    private bool  _audioConfigSent;
    private uint  _sampleRate;
    private ulong _audioNext90k;
    private TimestampUnwrapper _audioTs;

    private bool _videoUnsupportedLogged;

    /// <summary>Set when frames were written since the last flush; the receiver flushes and clears it.</summary>
    public bool HasPendingFrames { get; set; }

    public void OnProgramMapped(byte videoStreamType, byte audioStreamType)
    {
        switch (videoStreamType)
        {
            case M2TSStreamType.H264:
                context.VideoCodec = VideoCodec.H264; // triggers the pipeline wiring
                break;
            case M2TSStreamType.H265:
                context.VideoCodec = VideoCodec.H265;
                break;
            case 0:
                s_logger.ZLogError($"The TS program has no video track; audio-only ingest is not supported yet");
                break;
            default:
                if (!_videoUnsupportedLogged)
                {
                    s_logger.ZLogError($"Unsupported TS video stream_type 0x{videoStreamType:X2}");
                    _videoUnsupportedLogged = true;
                }
                break;
        }

        if (audioStreamType == M2TSStreamType.AdtsAac)
        {
            context.AudioCodec = AudioCodec.AAC;
        }
    }

    public void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
    {
        if (context.MediaOutlet is null)
        {
            return; // not wired (unsupported program); nothing to do
        }

        switch (streamType)
        {
            case M2TSStreamType.H264:
                OnH264AccessUnit(pts90k, dts90k, es);
                break;
            case M2TSStreamType.H265:
                OnH265AccessUnit(pts90k, dts90k, es);
                break;
            case M2TSStreamType.AdtsAac:
                OnAdtsFrames(pts90k, es);
                break;
        }
    }

    // =======================================================================
    // H.264

    private void OnH264AccessUnit(ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
    {
        (uint tsMs, int ctMs) = ResolveVideoTimestamps(pts90k, dts90k);

        _sample.ResetWrittenCount();
        var isKeyFrame = false;
        var parameterSetsChanged = false;

        foreach (ReadOnlySpan<byte> nalu in NALAnnexB.EnumerateNALUs(es))
        {
            var naluType = (byte)(nalu[0] & 0x1F);
            switch (naluType)
            {
                case 7: // SPS
                    parameterSetsChanged |= Capture(ref _sps, nalu);
                    continue;
                case 8: // PPS
                    parameterSetsChanged |= Capture(ref _pps, nalu);
                    continue;
                case 9: // access unit delimiter: re-added by muxers downstream
                    continue;
                case 5: // IDR
                    isKeyFrame = true;
                    break;
            }
            AppendSampleNalu(nalu);
        }

        if ((parameterSetsChanged || !_videoConfigSent) && _sps is not null && _pps is not null)
        {
            WriteFrame(MediaFrameKind.Video, MediaFrameFlags.Config, (uint)VideoCodec.H264, 0,
                BuildAvcC(_sps, _pps), tsMs);
            _videoConfigSent = true;
        }

        if (_sample.WrittenCount > 0 && _videoConfigSent)
        {
            WriteFrame(MediaFrameKind.Video,
                isKeyFrame ? MediaFrameFlags.KeyFrame : MediaFrameFlags.None,
                (uint)VideoCodec.H264, ctMs, _sample.WrittenSpan, tsMs);
        }
    }

    // =======================================================================
    // H.265

    private void OnH265AccessUnit(ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
    {
        (uint tsMs, int ctMs) = ResolveVideoTimestamps(pts90k, dts90k);

        _sample.ResetWrittenCount();
        var isKeyFrame = false;
        var parameterSetsChanged = false;

        foreach (ReadOnlySpan<byte> nalu in NALAnnexB.EnumerateNALUs(es))
        {
            var naluType = (byte)((nalu[0] >> 1) & 0x3F); // 2-byte NAL header in HEVC
            switch (naluType)
            {
                case 32: // VPS
                    parameterSetsChanged |= Capture(ref _vps, nalu);
                    continue;
                case 33: // SPS
                    parameterSetsChanged |= Capture(ref _sps, nalu);
                    continue;
                case 34: // PPS
                    parameterSetsChanged |= Capture(ref _pps, nalu);
                    continue;
                case 35: // access unit delimiter
                    continue;
                case >= 16 and <= 23: // IRAP (BLA/IDR/CRA and reserved IRAP)
                    isKeyFrame = true;
                    break;
            }
            AppendSampleNalu(nalu);
        }

        if ((parameterSetsChanged || !_videoConfigSent)
            && _vps is not null && _sps is not null && _pps is not null)
        {
            byte[] hvcc = HvcCBuilder.Build(_vps, _sps, _pps, out HvcCBuilder.SpsSummary sps);
            context.VideoWidth = sps.Width;
            context.VideoHeight = sps.Height;
            WriteFrame(MediaFrameKind.Video, MediaFrameFlags.Config, (uint)VideoCodec.H265, 0, hvcc, tsMs);
            _videoConfigSent = true;
        }

        if (_sample.WrittenCount > 0 && _videoConfigSent)
        {
            WriteFrame(MediaFrameKind.Video,
                isKeyFrame ? MediaFrameFlags.KeyFrame : MediaFrameFlags.None,
                (uint)VideoCodec.H265, ctMs, _sample.WrittenSpan, tsMs);
        }
    }

    // =======================================================================
    // shared video helpers

    private (uint TsMs, int CtMs) ResolveVideoTimestamps(ulong? pts90k, ulong? dts90k)
    {
        ulong rawDts = dts90k ?? pts90k ?? 0;
        ulong rawPts = pts90k ?? rawDts;
        ulong dts = _videoTs.Unwrap(rawDts);
        // PTS relative to DTS as a signed 33-bit distance, robust across the wrap
        long ct90 = (long)((rawPts - rawDts) & PtsMask);
        if (ct90 > (long)(PtsMask >> 1))
        {
            ct90 -= (long)(PtsMask + 1);
        }
        return ((uint)(dts / 90), (int)(ct90 / 90));
    }

    /// <summary>Canonical sample form: 4-byte big-endian length prefix per NALU.</summary>
    private void AppendSampleNalu(ReadOnlySpan<byte> nalu)
    {
        Span<byte> dest = _sample.GetSpan(4 + nalu.Length);
        BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)nalu.Length);
        nalu.CopyTo(dest[4..]);
        _sample.Advance(4 + nalu.Length);
    }

    private static bool Capture(ref byte[]? slot, ReadOnlySpan<byte> nalu)
    {
        if (slot is not null && nalu.SequenceEqual(slot))
        {
            return false;
        }
        slot = nalu.ToArray();
        return true;
    }

    private static byte[] BuildAvcC(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        // ISO 14496-15 AVCDecoderConfigurationRecord with one SPS and one PPS
        var avcc = new byte[11 + sps.Length + pps.Length];
        avcc[0] = 1;       // configurationVersion
        avcc[1] = sps[1];  // AVCProfileIndication
        avcc[2] = sps[2];  // profile_compatibility
        avcc[3] = sps[3];  // AVCLevelIndication
        avcc[4] = 0xFF;    // reserved(6) + lengthSizeMinusOne = 3 (4-byte lengths)
        avcc[5] = 0xE1;    // reserved(3) + numOfSequenceParameterSets = 1
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(6), (ushort)sps.Length);
        sps.CopyTo(avcc.AsSpan(8));
        int p = 8 + sps.Length;
        avcc[p] = 1;       // numOfPictureParameterSets
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(p + 1), (ushort)pps.Length);
        pps.CopyTo(avcc.AsSpan(p + 3));
        return avcc;
    }

    // =======================================================================
    // AAC (ADTS)

    private void OnAdtsFrames(ulong? pts90k, ReadOnlySpan<byte> es)
    {
        if (pts90k is { } pts)
        {
            _audioNext90k = _audioTs.Unwrap(pts);
        }

        while (es.Length >= 7)
        {
            if (es[0] != 0xFF || (es[1] & 0xF0) != 0xF0)
            {
                s_logger.ZLogWarning($"Lost ADTS sync; dropping the rest of the PES payload");
                return;
            }

            bool crcAbsent = (es[1] & 0x01) != 0;
            int headerLength = crcAbsent ? 7 : 9;
            int frameLength = ((es[3] & 0x03) << 11) | (es[4] << 3) | (es[5] >> 5);
            if (frameLength < headerLength || frameLength > es.Length)
            {
                s_logger.ZLogWarning($"Truncated ADTS frame ({frameLength} of {es.Length} bytes); dropped");
                return;
            }

            if (!_audioConfigSent)
            {
                var audioObjectType = (byte)(((es[2] >> 6) & 0x03) + 1);
                var samplingFrequencyIndex = (byte)((es[2] >> 2) & 0x0F);
                var channelConfiguration = (byte)(((es[2] & 0x01) << 2) | ((es[3] >> 6) & 0x03));

                Span<byte> asc =
                [
                    (byte)((audioObjectType << 3) | (samplingFrequencyIndex >> 1)),
                    (byte)((samplingFrequencyIndex << 7) | (channelConfiguration << 3)),
                ];
                _sampleRate = AudioSpecificConfig.Parse(asc).SampleRate;

                WriteFrame(MediaFrameKind.Audio, MediaFrameFlags.Config, (uint)AudioCodec.AAC, 0,
                    asc, (uint)(_audioNext90k / 90));
                _audioConfigSent = true;
            }

            WriteFrame(MediaFrameKind.Audio, MediaFrameFlags.None, (uint)AudioCodec.AAC, 0,
                es[headerLength..frameLength], (uint)(_audioNext90k / 90));

            // 1024 samples per AAC frame
            _audioNext90k += 1024UL * 90000 / _sampleRate;
            es = es[frameLength..];
        }
    }

    // =======================================================================

    private void WriteFrame(MediaFrameKind kind, MediaFrameFlags flags, uint codec, int compositionTimeMs,
        ReadOnlySpan<byte> payload, uint timestampMs)
    {
        var outlet = context.MediaOutlet!;
        MediaFrameHeader.Write(outlet, kind, flags, codec, compositionTimeMs, payload.Length, timestampMs);
        payload.CopyTo(outlet.GetSpan(payload.Length));
        outlet.Advance(payload.Length);
        HasPendingFrames = true;
    }

    /// <summary>Unwraps the 33-bit 90 kHz timestamps into a monotonic 64-bit timeline.</summary>
    private struct TimestampUnwrapper
    {
        private ulong _lastRaw;
        private ulong _epoch;
        private bool  _hasLast;

        public ulong Unwrap(ulong raw)
        {
            if (_hasLast && raw < _lastRaw && _lastRaw - raw > (PtsMask >> 1))
            {
                _epoch += PtsMask + 1;
            }
            _lastRaw = raw;
            _hasLast = true;
            return _epoch + raw;
        }
    }
}
