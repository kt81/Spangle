using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Spinner;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class Video
{
    private static readonly ILogger<Video> s_logger = SpangleLogManager.GetLogger<Video>();

    public static async ValueTask Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        // Parse control
        var ctrl = new FlvVideoControl(payload.FirstSpan[0]);
        context.IsEnhanced = ctrl.IsEnhanced; // aka. IsExHeader

        int headerLength;
        if (context.IsEnhanced)
        {
            var fourCCBuff = payload.Slice(1, 4);
            var fourCCVal = new BigEndianUInt32();
            fourCCBuff.CopyTo(fourCCVal.AsSpan());
            context.VideoCodec ??= FlvVideoCodecExtensions.ParseToInternal(fourCCVal.HostValue);
            headerLength = 5;
        }
        else
        {
            context.VideoCodec ??= ctrl.Codec.ToInternal();
            headerLength = 1;
        }

        if (context.MediaOutlet is null)
        {
            s_logger.ZLogWarning($"Video frame arrived before the media outlet is ready; dropped");
            return;
        }

        bool isKeyFrame = ctrl.FrameType is FlvVideoFrameType.Keyframe or FlvVideoFrameType.GeneratedKeyframe;
        var body = payload.Slice(headerLength);
        MediaFrameHeader.Write(context.MediaOutlet,
            MediaFrameKind.Video,
            isKeyFrame ? MediaFrameFlags.KeyFrame : MediaFrameFlags.None,
            (int)body.Length,
            context.Timestamp);

        var writeBuff = context.MediaOutlet.GetSpan((int)body.Length);
        body.CopyTo(writeBuff);
        context.MediaOutlet.Advance((int)body.Length);

        await context.MediaOutlet.FlushAsync(context.CancellationToken);
    }
}
