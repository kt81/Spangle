using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Unwraps an FLV AUDIODATA tag — the legacy AAC envelope or the enhanced-RTMP v2
/// FourCC envelope (Opus, ...) — and forwards the codec payload as a self-contained
/// <see cref="MediaFrameHeader"/> frame.
/// </summary>
internal abstract class Audio
{
    private static readonly ILogger<Audio> s_logger = SpangleLogManager.GetLogger<Audio>();

    public static async ValueTask HandleAsync(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // The assembled message is always a single segment
        var span = payload.FirstSpan;
        if (span.Length < 2)
        {
            throw new InvalidDataException($"AUDIODATA message too short: {span.Length} bytes");
        }
        var ctrl = new FlvAudioControl(span[0]);

        AudioCodec codec;
        MediaFrameFlags flags;
        ReadOnlySequence<byte> body;

        if (ctrl.Codec == FlvAudioCodec.ExHeader)
        {
            // enhanced-RTMP v2 envelope: [control(9 + AudioPacketType)][FourCC][data]
            if (span.Length < 5)
            {
                throw new InvalidDataException($"Enhanced AUDIODATA message too short: {span.Length} bytes");
            }
            uint fourCC = ((uint)span[1] << 24) | ((uint)span[2] << 16) | ((uint)span[3] << 8) | span[4];
            if (FlvAudioEnumExtensions.ParseAudioFourCc(fourCC) is not { } mapped)
            {
                if (!context.AudioUnsupportedLogged)
                {
                    context.AudioUnsupportedLogged = true;
                    s_logger.ZLogWarning($"Unsupported audio FourCC 0x{fourCC:X8}, audio is dropped");
                }
                return;
            }
            codec = mapped;

            var packetType = (FlvAudioPacketType)(span[0] & 0x0F);
            switch (packetType)
            {
                case FlvAudioPacketType.SequenceStart:
                    // Opus: the OpusHead identification header; AAC: the ASC
                    flags = MediaFrameFlags.Config;
                    break;
                case FlvAudioPacketType.CodedFrames:
                    flags = MediaFrameFlags.None;
                    break;
                case FlvAudioPacketType.SequenceEnd:
                case FlvAudioPacketType.MultichannelConfig:
                    // channel layouts beyond the codec config are presentation hints
                    s_logger.ZLogTrace($"Ignoring audio packet type {packetType}");
                    return;
                case FlvAudioPacketType.Multitrack:
                    if (!context.AudioUnsupportedLogged)
                    {
                        context.AudioUnsupportedLogged = true;
                        s_logger.ZLogWarning($"Multitrack audio is not supported; audio is dropped");
                    }
                    return;
                default:
                    throw new InvalidDataException($"Invalid FlvAudioPacketType: {packetType}");
            }
            body = payload.Slice(5);
        }
        else if (ctrl.Codec == FlvAudioCodec.AAC)
        {
            codec = AudioCodec.AAC;

            // AAC envelope: [control][AACPacketType][data]
            var packetType = (FlvAACPacketType)span[1];
            switch (packetType)
            {
                case FlvAACPacketType.AACSequenceHeader:
                    flags = MediaFrameFlags.Config;
                    break;
                case FlvAACPacketType.AACRaw:
                    flags = MediaFrameFlags.None;
                    break;
                default:
                    throw new InvalidDataException($"Invalid FlvAACPacketType: {packetType}");
            }
            body = payload.Slice(2);
        }
        else
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {ctrl.Codec}");
            return;
        }

        context.AudioCodec ??= codec;

        if (context.MediaOutlet is null)
        {
            if (flags == MediaFrameFlags.Config)
            {
                // The pipeline wires on the video codec, which may come after the
                // audio config. Losing it would mute the whole session, so keep it
                // and replay it once the outlet exists.
                context.PendingAudioConfig = body.ToArray();
                context.PendingAudioConfigCodec = codec;
                s_logger.ZLogDebug($"Audio config ({codec}) arrived before the media outlet; stashed");
            }
            else
            {
                s_logger.ZLogWarning($"Audio frame arrived before the media outlet is ready; dropped");
            }
            return;
        }

        FlushPendingConfig(context);

        // RTMP speaks milliseconds; the canonical frame clock is 90 kHz.
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Audio, flags, (uint)codec, 0, (int)body.Length, (long)context.Timestamp * 90);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays an audio config that arrived before the pipeline was wired.</summary>
    private static void FlushPendingConfig(RtmpReceiverContext context)
    {
        if (context.PendingAudioConfig is not { } config)
        {
            return;
        }
        context.PendingAudioConfig = null;

        MediaFrameHeader.Write(context.MediaOutlet!,
            MediaFrameKind.Audio, MediaFrameFlags.Config, (uint)context.PendingAudioConfigCodec, 0, config.Length, 0);
        var buff = context.MediaOutlet!.GetSpan(config.Length);
        config.CopyTo(buff);
        context.MediaOutlet.Advance(config.Length);
        s_logger.ZLogDebug($"Replayed the stashed audio config");
    }
}
