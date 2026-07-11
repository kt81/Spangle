using System.Buffers;
using Spangle.Transport.Rtmp.Amf0;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using Spangle.Util;

namespace Spangle.Transport.Rtmp;

public static class RtmpWriter
{
    private const int HeaderMaxSize = BasicHeader.MaxSize + MessageHeader.MaxSize;

    [ThreadStatic] private static ArrayBufferWriter<byte>? s_tempWriter;

    private static ArrayBufferWriter<byte> TempWriter =>
        s_tempWriter ??= new ArrayBufferWriter<byte>(1024);

    public static int Write<TPayload>(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, ref TPayload payload) where TPayload : unmanaged
    {
        int plLen = MarshalHelper<TPayload>.Size;
        int hLen = WriteHeader(context, timestampOrDelta, messageTypeId, chunkStreamId, streamId, plLen);
        var buff = context.RemoteWriter.GetSpan(plLen);

        // Write the payload
        unsafe
        {
            fixed (void* p = &payload)
            {
                new ReadOnlySpan<byte>(p, plLen).CopyTo(buff[..plLen]);
            }
        }

        context.RemoteWriter.Advance(plLen);

        return hLen + plLen;
    }

    public static int Write(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, IAmf0Serializable payload)
    {
        TempWriter.ResetWrittenCount(); // rewind without memset-ing the written area
        int plLen = payload.WriteBytes(TempWriter);
        int hLen = WriteHeader(context, timestampOrDelta, messageTypeId, chunkStreamId, streamId, plLen);

        // Split the payload into chunks of SendChunkSize, with a Fmt3 continuation header between chunks
        var span = TempWriter.WrittenSpan;
        var chunkSize = (int)context.SendChunkSize;
        var written = 0;
        while (true)
        {
            int len = Math.Min(chunkSize, plLen - written);
            context.RemoteWriter.Write(span.Slice(written, len));
            written += len;
            if (written >= plLen)
            {
                break;
            }

            hLen += WriteContinuationHeader(context, chunkStreamId);
        }

        return hLen + plLen;
    }

    private static int WriteContinuationHeader(RtmpReceiverContext context, uint chunkStreamId)
    {
        context.BasicHeaderToSend.Format = MessageHeaderFormat.Fmt3;
        context.BasicHeaderToSend.ChunkStreamId = chunkStreamId;
        int bhLen = context.BasicHeaderToSend.RequiredLength;
        var buff = context.RemoteWriter.GetSpan(bhLen);
        context.BasicHeaderToSend.AsSpan()[..bhLen].CopyTo(buff);
        context.RemoteWriter.Advance(bhLen);

        // 5.3.1.3: when the preceding header carried an extended timestamp, every
        // Fmt3 continuation chunk of the same message repeats it
        if (context.MessageHeaderToSend.HasExtendedTimestamp)
        {
            WriteExtendedTimestamp(context);
            bhLen += sizeof(uint);
        }
        return bhLen;
    }

    private static void WriteExtendedTimestamp(RtmpReceiverContext context)
    {
        var exTimeBuff = context.RemoteWriter.GetSpan(sizeof(uint));
        context.MessageHeaderToSend.ExtendedTimeStamp.AsSpan().CopyTo(exTimeBuff);
        // GetSpan may return more than requested; advancing by its full length would
        // splice uninitialized bytes into the send stream
        context.RemoteWriter.Advance(sizeof(uint));
    }

    private static int WriteHeader(RtmpReceiverContext context, uint timestampOrDelta, MessageType messageTypeId,
        uint chunkStreamId, uint streamId, int payloadLength)
    {
        var buff = context.RemoteWriter.GetSpan(HeaderMaxSize + payloadLength);
        MessageHeaderFormat fmt;

        // The first message on a new chunk stream must be Fmt0; the receiver has no reference header for it.
        bool chunkStreamChanged = context.BasicHeaderToSend.ChunkStreamId != chunkStreamId;
        context.BasicHeaderToSend.ChunkStreamId = chunkStreamId;

        if (chunkStreamChanged || context.MessageHeaderToSend.IsDefault || context.MessageHeaderToSend.StreamId != streamId)
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
        context.RemoteWriter.Advance(totalHeaderLength);

        if (context.MessageHeaderToSend.HasExtendedTimestamp)
        {
            WriteExtendedTimestamp(context);
            totalHeaderLength += sizeof(uint);
        }

        return totalHeaderLength;
    }
}
