using System.Buffers;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.ProtocolControlMessage;
using Spangle.Util;

namespace Spangle.Rtmp;

public static class RtmpWriter
{
    private const int HeaderMaxSize = BasicHeader.MaxSize + MessageHeader.MaxSize;

    public static int Write<TPayload>(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, ref TPayload payload) where TPayload : unmanaged
    {
        int plLen = MarshalHelper<TPayload>.Size;
        int hLen = WriteHeader(context, timestampOrDelta, messageTypeId, chunkStreamId, streamId, plLen);
        var buff = context.Writer.GetSpan(plLen);

        // Write the payload
        unsafe
        {
            fixed (void* p = &payload)
            {
                new ReadOnlySpan<byte>(p, plLen).CopyTo(buff[..plLen]);
            }
        }
        context.Writer.Advance(plLen);

        return hLen + plLen;
    }

    public static int Write(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, ReadOnlySpan<byte> amf0ByteSequence)
    {
        int hLen = WriteHeader(context, timestampOrDelta, messageTypeId, chunkStreamId, streamId, amf0ByteSequence.Length);
        context.Writer.Write(amf0ByteSequence);
        return hLen + amf0ByteSequence.Length;
    }

    private static int WriteHeader(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, int payloadLength)
    {
        var buff = context.Writer.GetSpan(HeaderMaxSize + payloadLength);
        MessageHeaderFormat fmt;

        context.BasicHeaderToSend.ChunkStreamId = chunkStreamId;

        if (context.MessageHeaderToSend.IsDefault || context.MessageHeaderToSend.StreamId != streamId)
        {
            // Force Fmt0
            context.BasicHeaderToSend.Format = fmt = MessageHeaderFormat.Fmt0;
            context.MessageHeaderToSend.SetFmt0(timestampOrDelta, (uint)payloadLength, messageTypeId, streamId);
        }
        else
        {
            // Fmt1. This writer does not support Fmt2-3
            context.BasicHeaderToSend.Format = fmt = MessageHeaderFormat.Fmt1;
            context.MessageHeaderToSend.SetFmt1(timestampOrDelta, (uint)payloadLength, messageTypeId);
        }

        // Write headers only for the required parts
        // BasicHeader
        int bhLen = context.BasicHeaderToSend.RequiredLength;
        context.BasicHeaderToSend.AsSpan()[..bhLen].CopyTo(buff);
        // MessageHeader
        int mhLen = fmt.GetMessageHeaderLength();
        context.MessageHeaderToSend.AsSpan()[..mhLen].CopyTo(buff.Slice(bhLen, mhLen));
        int totalHeaderLength = bhLen + mhLen;
        context.Writer.Advance(totalHeaderLength);

        if (context.MessageHeaderToSend.HasExtendedTimestamp)
        {
            var exTimeBuff = context.Writer.GetSpan(sizeof(uint));
            context.MessageHeaderToSend.ExtendedTimeStamp.AsSpan().CopyTo(exTimeBuff);
            context.Writer.Advance(exTimeBuff.Length);
        }

        return totalHeaderLength;
    }
}
