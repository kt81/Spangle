using Spangle.Rtmp.Chunk;
using Spangle.Util;

namespace Spangle.Rtmp;

public static class RtmpWriter
{
    private const int HeaderMaxSize = BasicHeader.MaxSize + MessageHeader.MaxSize;

    public static int Write<TPayload>(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, ref TPayload payload) where TPayload : unmanaged
    {
        int plLen = MarshalHelper<TPayload>.Size;
        var buff = context.Writer.GetSpan(HeaderMaxSize + plLen);
        MessageHeaderFormat fmt;

        context.BasicHeaderToSend.ChunkStreamId = chunkStreamId;

        if (context.MessageHeaderToSend.IsDefault || context.MessageHeaderToSend.StreamId != streamId)
        {
            // Force Fmt0
            context.BasicHeaderToSend.Format = fmt = MessageHeaderFormat.Fmt0;
            context.MessageHeaderToSend.SetFmt0(timestampOrDelta, (uint)plLen, messageTypeId, streamId);
        }
        else
        {
            // Fmt1. This writer does not support Fmt2-3
            context.BasicHeaderToSend.Format = fmt = MessageHeaderFormat.Fmt1;
            context.MessageHeaderToSend.SetFmt1(timestampOrDelta, (uint)plLen, messageTypeId);
        }

        // Write headers only for the required parts
        // BasicHeader
        int toAdvance = context.BasicHeaderToSend.RequiredLength;
        context.BasicHeaderToSend.ToSpan()[..toAdvance].CopyTo(buff);
        // MessageHeader
        int mhLen = fmt.GetMessageHeaderLength();
        context.MessageHeaderToSend.ToSpan()[..mhLen].CopyTo(buff.Slice(toAdvance, mhLen));
        toAdvance += mhLen;

        // Write the payload
        unsafe
        {
            fixed (void* p = &payload)
            {
                new ReadOnlySpan<byte>(p, plLen).CopyTo(buff.Slice(toAdvance, plLen));
            }
        }

        toAdvance += plLen;
        context.Writer.Advance(toAdvance);
        return toAdvance;
    }
}
