using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AAC;
using Spangle.Codecs.AVC;
using Spangle.Codecs.HEVC;
using Spangle.Logging;
using Spangle.Spinner;
using Spangle.Transport.Rtsp.Rtp;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp;

/// <summary>
/// Turns depacketized RTP access units into the application's canonical
/// <see cref="MediaFrameHeader"/> frames — the same shape the RTMP and TS receivers
/// emit — so the whole downstream pipeline (spinners, TS/CMAF/DASH) works unchanged:
/// length-prefixed NALU samples + an avcC/hvcC Config frame built from the SDP
/// parameter sets (or in-band ones), and raw AAC frames + an AudioSpecificConfig
/// Config frame from the SDP fmtp.
/// </summary>
internal sealed class RtspMediaFrameAdapter<TContext>
    where TContext : ReceiverContextBase<TContext>
{
    private static readonly ILogger s_logger =
        SpangleLogManager.GetLogger<RtspMediaFrameAdapter<TContext>>();

    private readonly ReceiverContextBase<TContext> _context;
    private readonly RtspTimelineSync _sync = new();
    private readonly ArrayBufferWriter<byte> _sample = new(64 * 1024);

    private VideoTrack? _video;
    private AudioTrack? _audio;

    public RtspMediaFrameAdapter(ReceiverContextBase<TContext> context)
    {
        _context = context;
    }

    public bool HasVideo => _video is not null;
    public bool HasAudio => _audio is not null;

    /// <summary>Set when frames were written since the last flush; the receiver flushes and clears it.</summary>
    public bool HasPendingFrames { get; set; }

    // =======================================================================
    // track setup (from the SDP DESCRIBE answer)

    /// <summary>Wires a video track; returns false when the encoding is not supported.</summary>
    public bool SetupVideo(SdpMedia media)
    {
        uint clockRate = media.ClockRate == 0 ? 90000 : media.ClockRate;
        switch (media.Encoding)
        {
            case "H264":
                _video = new VideoTrack(VideoCodec.H264, new RtpTimeline(clockRate, _sync),
                    new H264Depacketizer(OnVideoAccessUnit));
                LoadH264ParameterSets(media);
                _context.VideoCodec = VideoCodec.H264; // triggers the pipeline wiring
                return true;

            case "H265":
            case "HEVC":
                _video = new VideoTrack(VideoCodec.H265, new RtpTimeline(clockRate, _sync),
                    new H265Depacketizer(OnVideoAccessUnit));
                LoadH265ParameterSets(media);
                _context.VideoCodec = VideoCodec.H265;
                return true;

            default:
                s_logger.ZLogWarning($"Unsupported RTSP video encoding `{media.Encoding}`");
                return false;
        }
    }

    /// <summary>Wires an audio track; returns false when the encoding is not supported.</summary>
    public bool SetupAudio(SdpMedia media)
    {
        uint clockRate = media.ClockRate == 0 ? 44100 : media.ClockRate;
        switch (media.Encoding)
        {
            case "MPEG4-GENERIC": // AAC-hbr (LATM/MP4A-LATM is a different framing, not handled)
                byte[]? asc = ParseHex(media.FmtpValue("config"));
                if (asc is null || asc.Length < 2)
                {
                    s_logger.ZLogWarning($"AAC track has no usable fmtp config=; audio ignored");
                    return false;
                }
                int sizeLength = FmtpInt(media, "sizeLength", 13);
                int indexLength = FmtpInt(media, "indexLength", 3);
                int indexDeltaLength = FmtpInt(media, "indexDeltaLength", 3);
                uint sampleRate = AudioSpecificConfig.Parse(asc).SampleRate;
                _audio = new AudioTrack(new RtpTimeline(clockRate, _sync), asc, sampleRate,
                    new AacDepacketizer(sizeLength, indexLength, indexDeltaLength, OnAacFrame));
                _context.AudioCodec = AudioCodec.AAC;
                return true;

            default:
                s_logger.ZLogWarning($"Unsupported RTSP audio encoding `{media.Encoding}`");
                return false;
        }
    }

    private void LoadH264ParameterSets(SdpMedia media)
    {
        // sprop-parameter-sets: comma-separated base64 NAL units (SPS,PPS)
        string? sprop = media.FmtpValue("sprop-parameter-sets");
        if (sprop is null)
        {
            return;
        }
        foreach (string part in sprop.Split(','))
        {
            byte[]? nal = ParseBase64(part);
            if (nal is not { Length: > 0 })
            {
                continue;
            }
            switch (nal[0] & 0x1F)
            {
                case 7: _video!.Sps = nal; break;
                case 8: _video!.Pps = nal; break;
            }
        }
    }

    private void LoadH265ParameterSets(SdpMedia media)
    {
        _video!.Vps = ParseBase64(media.FmtpValue("sprop-vps"));
        _video!.Sps = ParseBase64(media.FmtpValue("sprop-sps"));
        _video!.Pps = ParseBase64(media.FmtpValue("sprop-pps"));
    }

    // =======================================================================
    // depacketizer feeds (the receiver routes RTP packets here)

    public void FeedVideo(in RtpPacket packet) => _video?.Depacketizer.Feed(packet);

    public void FeedAudio(in RtpPacket packet) => _audio?.Depacketizer.Feed(packet);

    public void OnVideoSenderReport(in RtcpSenderReport report) => _video?.Timeline.OnSenderReport(report);

    public void OnAudioSenderReport(in RtcpSenderReport report) => _audio?.Timeline.OnSenderReport(report);

    /// <summary>PLAY's RTP-Info sets each track's session-time zero.</summary>
    public void SetVideoPlayBase(uint rtptime) => _video?.Timeline.SetPlayBase(rtptime);

    public void SetAudioPlayBase(uint rtptime) => _audio?.Timeline.SetPlayBase(rtptime);

    // =======================================================================
    // access-unit conversion

    private void OnVideoAccessUnit(NalAccessUnit unit)
    {
        if (_context.MediaOutlet is null || _video is null)
        {
            return;
        }
        uint tsMs = _video.Timeline.ToMilliseconds(unit.RtpTimestamp);

        _sample.ResetWrittenCount();
        var isKeyFrame = false;
        var parameterSetsChanged = false;

        foreach (byte[] nal in unit.Nals)
        {
            if (_video.Codec == VideoCodec.H264)
            {
                switch (nal[0] & 0x1F)
                {
                    case 7: parameterSetsChanged |= Capture(ref _video.Sps, nal); continue;
                    case 8: parameterSetsChanged |= Capture(ref _video.Pps, nal); continue;
                    case 9: continue; // AUD
                    case 5: isKeyFrame = true; break;
                }
            }
            else
            {
                switch ((nal[0] >> 1) & 0x3F)
                {
                    case 32: parameterSetsChanged |= Capture(ref _video.Vps, nal); continue;
                    case 33: parameterSetsChanged |= Capture(ref _video.Sps, nal); continue;
                    case 34: parameterSetsChanged |= Capture(ref _video.Pps, nal); continue;
                    case 35: continue; // AUD
                    case >= 16 and <= 23: isKeyFrame = true; break;
                }
            }
            AppendSampleNalu(nal);
        }

        EmitVideoConfigIfReady(parameterSetsChanged, tsMs);

        if (_sample.WrittenCount > 0 && _video.ConfigSent)
        {
            WriteFrame(MediaFrameKind.Video, isKeyFrame ? MediaFrameFlags.KeyFrame : MediaFrameFlags.None,
                (uint)_video.Codec, 0, _sample.WrittenSpan, tsMs);
        }
    }

    private void EmitVideoConfigIfReady(bool parameterSetsChanged, uint tsMs)
    {
        if (_video is null || (_video.ConfigSent && !parameterSetsChanged))
        {
            return;
        }

        if (_video.Codec == VideoCodec.H264 && _video.Sps is { } sps264 && _video.Pps is { } pps264)
        {
            try
            {
                (_context.VideoWidth, _context.VideoHeight) = AvcSps.ParseDimensions(sps264);
            }
            catch (InvalidDataException e)
            {
                s_logger.ZLogWarning($"Could not read the H.264 SPS dimensions: {e.Message}");
            }
            WriteFrame(MediaFrameKind.Video, MediaFrameFlags.Config, (uint)VideoCodec.H264, 0,
                AvcCBuilder.Build(sps264, pps264), tsMs);
            _video.ConfigSent = true;
        }
        else if (_video.Codec == VideoCodec.H265
                 && _video.Vps is { } vps && _video.Sps is { } sps265 && _video.Pps is { } pps265)
        {
            byte[] hvcc = HvcCBuilder.Build(vps, sps265, pps265, out HvcCBuilder.SpsSummary summary);
            _context.VideoWidth = summary.Width;
            _context.VideoHeight = summary.Height;
            WriteFrame(MediaFrameKind.Video, MediaFrameFlags.Config, (uint)VideoCodec.H265, 0, hvcc, tsMs);
            _video.ConfigSent = true;
        }
    }

    private void OnAacFrame(byte[] au, uint rtpTimestamp, int indexInPacket)
    {
        if (_context.MediaOutlet is null || _audio is null)
        {
            return;
        }
        // each AU in a packet is one 1024-sample block after the packet's timestamp
        uint baseMs = _audio.Timeline.ToMilliseconds(rtpTimestamp);
        uint tsMs = baseMs + (uint)(indexInPacket * 1024L * 1000 / _audio.SampleRate);

        if (!_audio.ConfigSent)
        {
            WriteFrame(MediaFrameKind.Audio, MediaFrameFlags.Config, (uint)AudioCodec.AAC, 0,
                _audio.AudioSpecificConfig, tsMs);
            _audio.ConfigSent = true;
        }
        WriteFrame(MediaFrameKind.Audio, MediaFrameFlags.None, (uint)AudioCodec.AAC, 0, au, tsMs);
    }

    // =======================================================================
    // helpers

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

    private void WriteFrame(MediaFrameKind kind, MediaFrameFlags flags, uint codec, int compositionTimeMs,
        ReadOnlySpan<byte> payload, uint timestampMs)
    {
        var outlet = _context.MediaOutlet!;
        MediaFrameHeader.Write(outlet, kind, flags, codec, compositionTimeMs, payload.Length, timestampMs);
        payload.CopyTo(outlet.GetSpan(payload.Length));
        outlet.Advance(payload.Length);
        HasPendingFrames = true;
    }

    private static int FmtpInt(SdpMedia media, string key, int fallback) =>
        int.TryParse(media.FmtpValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static byte[]? ParseBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? ParseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return Convert.FromHexString(value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class VideoTrack(VideoCodec codec, RtpTimeline timeline, IVideoDepacketizer depacketizer)
    {
        public VideoCodec Codec { get; } = codec;
        public RtpTimeline Timeline { get; } = timeline;
        public IVideoDepacketizer Depacketizer { get; } = depacketizer;
        public byte[]? Vps;
        public byte[]? Sps;
        public byte[]? Pps;
        public bool ConfigSent;
    }

    private sealed class AudioTrack(RtpTimeline timeline, byte[] asc, uint sampleRate, AacDepacketizer depacketizer)
    {
        public RtpTimeline Timeline { get; } = timeline;
        public byte[] AudioSpecificConfig { get; } = asc;
        public uint SampleRate { get; } = sampleRate;
        public AacDepacketizer Depacketizer { get; } = depacketizer;
        public bool ConfigSent;
    }
}
