﻿using Cysharp.Text;

namespace Spangle.Rtmp.Chunk.Processor;

public interface IChunkProcessor
{
    public static abstract ValueTask PerformProcess(RtmpReceiverContext context);

    protected static void EnsureValidProtocolControlMessage(RtmpReceiverContext context)
    {
        if (context.MessageHeader.StreamId == RtmpReceiver.ControlStreamId /* &&
            context.BasicHeader.ChunkStreamId == ControlChunkStreamId */)
        {
            return;
        }

        throw new IOException(ZString.Format("Invalid streamId({0}) or chunkStreamId({1}) for Protocol Control Message",
            context.MessageHeader.StreamId, context.BasicHeader.ChunkStreamId));
    }
}