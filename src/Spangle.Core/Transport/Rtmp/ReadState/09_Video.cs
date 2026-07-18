using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Unwraps an FLV VIDEODATA tag (both classic AVC and enhanced-RTMP envelopes) and
/// forwards the codec payload as a self-contained <see cref="MediaFrameHeader"/> frame.
/// </summary>
internal abstract class Video
{
    private static readonly ILogger<Video> s_logger = SpangleLogManager.GetLogger<Video>();

    public static async ValueTask HandleAsync(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // The assembled message is always a single segment
        var span = payload.FirstSpan;
        if (span.Length < 2)
        {
            throw new InvalidDataException($"VIDEODATA message too short: {span.Length} bytes");
        }
        var ctrl = new FlvVideoControl(span[0]);
        context.IsEnhanced = ctrl.IsEnhanced; // aka. IsExHeader

        VideoCodec codec;
        MediaFrameFlags flags;
        var compositionTimeMs = 0;
        ReadOnlySequence<byte> body;

        if (!ctrl.IsEnhanced)
        {
            // Classic envelope: [control][AVCPacketType][CompositionTime SI24][data]
            if (span.Length < 5)
            {
                throw new InvalidDataException($"AVC VIDEODATA message too short: {span.Length} bytes");
            }
            codec = ctrl.Codec.ToInternal();
            var packetType = (FlvAVCPacketType)span[1];
            switch (packetType)
            {
                case FlvAVCPacketType.SequenceHeader:
                    flags = MediaFrameFlags.Config;
                    break;
                case FlvAVCPacketType.Nalu:
                    flags = ToKeyFrameFlag(ctrl.FrameType);
                    compositionTimeMs = ReadSi24(span[2..5]);
                    break;
                case FlvAVCPacketType.EndOfSequence:
                    return;
                default:
                    throw new InvalidDataException($"Invalid FlvAVCPacketType: {packetType}");
            }
            body = payload.Slice(5);
        }
        else
        {
            // enhanced-RTMP envelope: [control(isEx+packetType)][FourCC][per-packet-type fields][data]
            if (span.Length < 5)
            {
                throw new InvalidDataException($"Enhanced VIDEODATA message too short: {span.Length} bytes");
            }
            uint fourCC = ((uint)span[1] << 24) | ((uint)span[2] << 16) | ((uint)span[3] << 8) | span[4];
            codec = FlvVideoCodecExtensions.ParseToInternal(fourCC);
            switch (ctrl.VideoPacketType)
            {
                case FlvVideoPacketType.PacketTypeSequenceStart:
                    flags = MediaFrameFlags.Config;
                    body = payload.Slice(5);
                    break;
                case FlvVideoPacketType.PacketTypeCodedFrames:
                    flags = ToKeyFrameFlag(ctrl.FrameType);
                    if (codec is VideoCodec.H264 or VideoCodec.H265)
                    {
                        // The SI24 composition time is only present for AVC/HEVC
                        if (span.Length < 8)
                        {
                            throw new InvalidDataException($"CodedFrames message too short: {span.Length} bytes");
                        }
                        compositionTimeMs = ReadSi24(span[5..8]);
                        body = payload.Slice(8);
                    }
                    else
                    {
                        body = payload.Slice(5);
                    }
                    break;
                case FlvVideoPacketType.PacketTypeCodedFramesX:
                    // CompositionTime is implied to be zero
                    flags = ToKeyFrameFlag(ctrl.FrameType);
                    body = payload.Slice(5);
                    break;
                case FlvVideoPacketType.PacketTypeSequenceEnd:
                    return;
                case FlvVideoPacketType.PacketTypeMetadata:
                    s_logger.ZLogTrace($"Ignoring video metadata frame");
                    return;
                default:
                    throw new NotInScopeException($"Unsupported video packet type: {ctrl.VideoPacketType}");
            }
        }

        if (context.IsAudioOnly)
        {
            // the audio-only fallback already wired the session without a video track
            if (!context.VideoAfterAudioOnlyLogged)
            {
                context.VideoAfterAudioOnlyLogged = true;
                s_logger.ZLogWarning(
                    $"Video appeared after the session was declared audio-only (it outwaited the fallback); video is dropped");
            }
            return;
        }

        context.VideoCodec ??= codec;

        if (context.MediaOutlet is null)
        {
            s_logger.ZLogWarning($"Video frame arrived before the media outlet is ready; dropped");
            return;
        }

        // RTMP speaks milliseconds; the canonical frame clock is 90 kHz.
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Video, flags, (uint)codec, compositionTimeMs * 90, (int)body.Length,
            (long)context.Timestamp * 90);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    private static MediaFrameFlags ToKeyFrameFlag(FlvVideoFrameType frameType) =>
        frameType is FlvVideoFrameType.Keyframe or FlvVideoFrameType.GeneratedKeyframe
            ? MediaFrameFlags.KeyFrame
            : MediaFrameFlags.None;

    private static int ReadSi24(ReadOnlySpan<byte> b)
    {
        int value = (b[0] << 16) | (b[1] << 8) | b[2];
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;
    }
}
