using System.Buffers;
using System.Runtime.CompilerServices;
using Spangle.IO;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    /// <summary>
    /// MessageHeaderProcessor
    /// <see cref="ChunkMessageHeader"/>
    /// </summary>
    private class MessageHeaderProcessor : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var reader = context._reader;
            int len = context._chunk.BasicHeader.Format.GetLength();
            if (len == 0)
            {
                // Continue with recent header.
                DispatchNext(context);
                return;
            }

            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(len, ct);
            unsafe
            {
                var p = (byte*)Unsafe.AsPointer(ref context._chunk.MessageHeader);
                buff.FirstSpan.CopyTo(new Span<byte>(p, len));
            }
            reader.AdvanceTo(buff.End);

            DispatchNext(context);
        }

        private static void DispatchNext(ChunkReader context)
        {
            switch (context._chunk.MessageHeader.TypeId)
            {
                case MessageType.SetChunkSize:
                    context.Next<SetChunkSize>();
                    break;
                case MessageType.CommandAmf0:
                    // context.Next<>();
                    throw new NotImplementedException();
                default:
                    throw new NotImplementedException($"The processor of [{context._chunk.MessageHeader.TypeId}] is not implemented.");
            }
        }
    }
}
