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
            var len = context._chunk.BasicHeader.Format.GetLength();
            if (len == 0)
            {
                context.Next<BodyParser>();
                return;
            }
            
            var (buff, _) = await reader.ReadExactlyAsync(len, ct);
            unsafe
            {
                var p = (byte*)Unsafe.AsPointer(ref context._chunk.MessageHeader);
                buff.FirstSpan.CopyTo(new Span<byte>(p, len));
            }
            reader.AdvanceTo(buff.End);
            context.Next<BodyParser>();
        }
    }
}
