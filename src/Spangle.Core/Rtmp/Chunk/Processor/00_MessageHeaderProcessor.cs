using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Spangle.IO;

namespace Spangle.Rtmp.Chunk.Processor;

/// <summary>
///     MessageHeaderProcessor
///     <see cref="ChunkMessageHeader" />
/// </summary>
internal abstract class MessageHeaderProcessor : IChunkProcessor
{
    public static async ValueTask PerformProcess(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        int len = context.BasicHeader.Format.GetLength();
        if (len == 0)
        {
            // Continue with recent header.
            DispatchNextByMessageHeader(context);
            return;
        }

        (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(len, ct);
        unsafe
        {
            var p = (byte*)Unsafe.AsPointer(ref context.MessageHeader);
            buff.FirstSpan.CopyTo(new Span<byte>(p, len));
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
