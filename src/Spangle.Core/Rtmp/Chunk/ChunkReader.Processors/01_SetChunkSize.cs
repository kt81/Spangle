using System.Buffers;
using Spangle.IO;
using Spangle.IO.Interop;
using ZLogger;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class SetChunkSize : IChunkProcessor
    {
        private const int ChunkSizeLength = 4; // 32bit Big Endian uint

        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var reader = context._reader;
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(ChunkSizeLength, ct);
            uint size = BufferMarshal.As<BigEndianUInt32>(buff).HostValue;
            reader.AdvanceTo(buff.End);
            EnsureValidProtocolControlMessage(context);

            if (size is 0 or > 0x0FFFFFFF)
            {
                context._logger.ZLogError("Invalid chunk size: {0}", size);
                throw new Exception();
            }

            context._maxChunkSize = size;
            context.Next<BasicHeaderProcessor>();
        }
    }
}
