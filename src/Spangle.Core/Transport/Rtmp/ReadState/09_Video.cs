using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class Video : IReadStateAction
{
    private static readonly ILogger<Video> s_logger = SpangleLogManager.GetLogger<Video>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;

        await using var enumerator = ReadHelper.ReadChunkedMessageBody(context).GetAsyncEnumerator(ct);
        await enumerator.MoveNextAsync();
        var buff = enumerator.Current;

        Debug.Assert(context.VideoOutlet != null, "context.VideoOutlet has not been set. It may be a bug.");

        var packetType = ReadVideoTagHeader(context, ref buff);
        reader.AdvanceTo(buff.Start);

        if (buff.Length != 0)
        {
            // Write the rest of the buffer
            ReadAndWriteTagBody(context, ref buff, packetType);
            reader.AdvanceTo(buff.End);
        }
        while (await enumerator.MoveNextAsync())
        {
            buff = enumerator.Current;
            ReadAndWriteTagBody(context, ref buff, packetType);
            reader.AdvanceTo(buff.End);
        }

        await context.VideoOutlet!.FlushAsync(ct);

        // Read all chunks
        while (await enumerator.MoveNextAsync())
        {
            buff = enumerator.Current;
            reader.AdvanceTo(buff.End);
        }

        // switch (control.Codec.ToInternal())
        // {
        //     case VideoCodec.H264:
        // }
        // buff.ToArray().DumpHex(s_logger.ZLogDebug);
        // Advance to last buffer's End position

        context.SetNext<ReadChunkHeader>();
    }

    private static FlvVideoPacketType ReadVideoTagHeader(RtmpReceiverContext context, ref ReadOnlySequence<byte> buff)
    {
        // Parse control
        var ctrl = new FlvVideoControl(buff.FirstSpan[0]);
        context.IsEnhanced = ctrl.IsEnhanced; // aka. IsExHeader
        s_logger.ZLogDebug(
            $$"""FlvVideoControl {frameType:{{ctrl.FrameType}}, codec:{{ctrl.Codec}}, isEnhanced:{{context.IsEnhanced}}}""");

        FlvVideoPacketType packetType;
        SequencePosition endPos;
        int headerLength;

        if (context.IsEnhanced)
        {
            // The field of CodecID is treated as PacketType if IsExHeader == true
            packetType = ctrl.VideoPacketType;
            var fourCCBuff = buff.Slice(buff.GetPosition(1), 4);
            var fourCCVal = new BigEndianUInt32();
            fourCCBuff.CopyTo(fourCCVal.AsSpan());
            endPos = fourCCBuff.End;
            context.VideoCodec ??= FlvVideoCodecExtensions.ParseToInternal(fourCCVal.HostValue);
            headerLength = 5;
        }
        else
        {
            // IsExHeader == false
            context.VideoCodec ??= ctrl.Codec.ToInternal();
            // The value is invalid. It must be ignored.
            packetType = FlvVideoPacketType.PacketTypeSequenceStart;
            endPos = buff.GetPosition(1);
            headerLength = 1;
        }

        context.VideoTagLengthQueue.Enqueue((int)context.MessageHeader.Length.HostValue - headerLength);

        buff = buff.Slice(endPos);
        return packetType;
    }

    private static void ReadAndWriteTagBody(RtmpReceiverContext context, ref ReadOnlySequence<byte> buff, FlvVideoPacketType packetType)
    {
        var writeBuff = context.VideoOutlet!.GetSpan((int)buff.Length);
        buff.CopyTo(writeBuff);
        context.VideoOutlet.Advance((int)buff.Length);
    }
}
