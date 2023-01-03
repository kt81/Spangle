using System.Runtime.CompilerServices;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class MessageHeaderProcessor : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var stream = context._reader;
            var buff = context._chunkBuffer;
            var idx = 0;
            var len = context._chunk.BasicHeader.Format.GetLength();
            if (len == 0)
            {
                // TODO どうしようもあらへん
                context.Next<BodyParser>();
            }
            
            await stream.ReadExactlyAsync(buff.AsMemory(idx, len), ct);
            unsafe
            {
                var p = (byte*)Unsafe.AsPointer(ref context._chunk.MessageHeader);
                buff.AsSpan(idx, len).CopyTo(new Span<byte>(p, len));
            }

             // header = ref context._chunk.MessageHeader;
        }
    }
}
