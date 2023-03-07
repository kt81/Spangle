using System.Runtime.CompilerServices;
using Cysharp.Text;

namespace Spangle.Protocols.Rtmp.ReadState;

internal interface IReadStateAction
{
    internal delegate ValueTask Action(RtmpReceiverContext receiverContext);

    public static abstract ValueTask Perform(RtmpReceiverContext context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static void EnsureValidProtocolControlMessage(RtmpReceiverContext context)
    {
        if (context.MessageHeader.StreamId == Protocol.ControlStreamId /* &&
            context.BasicHeader.ChunkStreamId == ControlChunkStreamId */)
        {
            return;
        }

        throw new IOException(ZString.Format("Invalid streamId({0}) or chunkStreamId({1}) for Protocol Control Message",
            context.MessageHeader.StreamId, context.BasicHeader.ChunkStreamId));
    }
}
