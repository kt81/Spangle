using System.Buffers;
using Cysharp.Text;
using Spangle.Interop;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class SetChunkSize
{
    public static void Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        uint size = BufferMarshal.As<BigEndianUInt32>(payload.Slice(0, 4)).HostValue;
        Protocol.EnsureValidProtocolControlMessage(context);

        if (size is < Protocol.MinChunkSize or > Protocol.MaxChunkSize)
        {
            throw new InvalidOperationException(ZString.Format("Invalid chunk size: {0}", size));
        }

        context.ChunkSize = size;
    }
}
