using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class Audio
{
    private static readonly ILogger<Audio> s_logger = SpangleLogManager.GetLogger<Audio>();

    public static async ValueTask Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // Parse control
        var c = new FlvAudioControl(payload.FirstSpan[0]);

        if (c.Codec != FlvAudioCodec.AAC)
        {
            s_logger.ZLogWarning($"Unsupported audio codec, dropping: {c.Codec}");
            return;
        }
        if (context.MediaOutlet is null)
        {
            s_logger.ZLogWarning($"Audio frame arrived before the media outlet is ready; dropped");
            return;
        }

        context.AudioCodec ??= c.Codec.ToInternal();

        // Forward AACAUDIODATA ([AACPacketType][data]) without the control byte
        var body = payload.Slice(1);
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Audio,
            MediaFrameFlags.None,
            (int)body.Length,
            context.Timestamp);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken);
    }
}
