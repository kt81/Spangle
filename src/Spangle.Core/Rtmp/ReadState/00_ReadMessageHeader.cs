using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Spangle.IO;
using Spangle.Rtmp.Chunk;

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
            DispatchNextByMessageHeader(context);
            return;
        }

        (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(len, ct);
        unsafe {
            fixed (void* p = &context.MessageHeader)
            {
                buff.CopyTo(context.MessageHeader.ToSpan());
            }
        }

        reader.AdvanceTo(buff.End);

        DispatchNextByMessageHeader(context);
    }

    private static void DispatchNextByMessageHeader(RtmpReceiverContext context)
    {
        switch (context.MessageHeader.TypeId)
        {
            case MessageType.SetChunkSize:
                context.SetNext<SetChunkSize>();
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
