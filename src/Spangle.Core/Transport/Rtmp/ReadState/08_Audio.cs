using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Unwraps an FLV AUDIODATA tag and forwards the codec payload as a
/// self-contained <see cref="MediaFrameHeader"/> frame.
/// </summary>
internal abstract class Audio
{
    private static readonly ILogger<Audio> s_logger = SpangleLogManager.GetLogger<Audio>();

    public static async ValueTask Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // The assembled message is always a single segment
        var span = payload.FirstSpan;
        if (span.Length < 2)
        {
            throw new InvalidDataException($"AUDIODATA message too short: {span.Length} bytes");
        }
        var ctrl = new FlvAudioControl(span[0]);

        if (ctrl.Codec != FlvAudioCodec.AAC)
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {ctrl.Codec}");
            return;
        }

        var codec = ctrl.Codec.ToInternal();
        context.AudioCodec ??= codec;

        // AAC envelope: [control][AACPacketType][data]
        var packetType = (FlvAACPacketType)span[1];
        MediaFrameFlags flags;
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

        var body = payload.Slice(2);

        if (context.MediaOutlet is null)
        {
            if (flags == MediaFrameFlags.Config)
            {
                // The pipeline wires on the video codec, which may come after the AAC
                // sequence header. Losing it would mute the whole session, so keep it
                // and replay it once the outlet exists.
                context.PendingAudioConfig = body.ToArray();
                s_logger.ZLogDebug($"AAC sequence header arrived before the media outlet; stashed");
            }
            else
            {
                s_logger.ZLogWarning($"Audio frame arrived before the media outlet is ready; dropped");
            }
            return;
        }

        FlushPendingConfig(context);

        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Audio, flags, (uint)codec, 0, (int)body.Length, context.Timestamp);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken);
    }

    /// <summary>Replays an AAC sequence header that arrived before the pipeline was wired.</summary>
    private static void FlushPendingConfig(RtmpReceiverContext context)
    {
        if (context.PendingAudioConfig is not { } config)
        {
            return;
        }
        context.PendingAudioConfig = null;

        MediaFrameHeader.Write(context.MediaOutlet!,
            MediaFrameKind.Audio, MediaFrameFlags.Config, (uint)AudioCodec.AAC, 0, config.Length, 0);
        var buff = context.MediaOutlet!.GetSpan(config.Length);
        config.CopyTo(buff);
        context.MediaOutlet.Advance(config.Length);
        s_logger.ZLogDebug($"Replayed the stashed AAC sequence header");
    }
}
