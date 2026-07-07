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
        var ctrl = new FlvAudioControl(span[0]);

        if (ctrl.Codec != FlvAudioCodec.AAC)
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {ctrl.Codec}");
            return;
        }
        if (context.MediaOutlet is null)
        {
            s_logger.ZLogWarning($"Audio frame arrived before the media outlet is ready; dropped");
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
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Audio, flags, (uint)codec, 0, (int)body.Length, context.Timestamp);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken);
    }
}
