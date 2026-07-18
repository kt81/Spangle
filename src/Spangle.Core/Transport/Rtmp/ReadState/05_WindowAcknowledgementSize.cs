using System.Buffers;
using Spangle.Interop;

namespace Spangle.Transport.Rtmp.ReadState;

/// <summary>
/// Window Acknowledgement Size (RTMP 5.4.4): the peer tells us how many bytes it will send before
/// it expects an <see cref="ProtocolControlMessage.MessageType.Acknowledgement"/> in return. We
/// record it; <see cref="RtmpReceiverContext.MaybeAcknowledgeAsync"/> then acknowledges every
/// window's worth of received bytes.
/// </summary>
internal abstract class WindowAcknowledgementSize
{
    public static void Handle(RtmpReceiverContext context, ReadOnlySequence<byte> payload)
    {
        uint size = BufferMarshal.As<BigEndianUInt32>(payload.Slice(0, 4)).HostValue;
        Protocol.EnsureValidProtocolControlMessage(context);
        context.SetPeerAckWindowSize(size);
    }
}
