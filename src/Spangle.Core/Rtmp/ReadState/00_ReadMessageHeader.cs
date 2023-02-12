using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.ProtocolControlMessage;

namespace Spangle.Rtmp.ReadState;

/// <summary>
///     ReadMessageHeader
///     <see cref="MessageHeader" />
/// </summary>
internal abstract class ReadMessageHeader : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        int len = context.BasicHeader.Format.GetMessageHeaderLength();
        if (len == 0)
        {
            // Continue with recent header.
            DispatchNextByMessageType(context);
            return;
        }

        (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(len, ct);
        buff.CopyTo(context.MessageHeader.AsSpan());
        reader.AdvanceTo(buff.End);

        if (context.MessageHeader.HasExtendedTimestamp)
        {
            // Requires reading of extended timestamp
            (buff, _) = await reader.ReadExactlyAsync(sizeof(uint), ct);
            buff.CopyTo(context.MessageHeader.ExtendedTimeStamp.AsSpan());
            reader.AdvanceTo(buff.End);
        }

        DispatchNextByMessageType(context);
    }

    private static void DispatchNextByMessageType(RtmpReceiverContext context)
    {
        switch (context.MessageHeader.TypeId)
        {
            case MessageType.SetChunkSize:
                context.SetNext<SetChunkSize>();
                break;
            case MessageType.DataAmf0:
                context.SetNext<DataAmf0>();
                break;
            case MessageType.CommandAmf0:
                context.SetNext<CommandAmf0>();
                break;
            default:
                throw new NotImplementedException(
                    $"The processor of [{context.MessageHeader.TypeId}] is not implemented.");
        }
    }
}
