using System.Buffers;
using Spangle.IO;
using Spangle.IO.Interop;
using Spangle.Rtmp.Amf0;
using ZLogger;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class CommandAmf0 : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var reader = context._reader;
            (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync((int)context._chunk.MessageHeader.Length, ct);
            EnsureValidProtocolControlMessage(context);
            Amf0CommandParser.ParseCommand(ref buff);
            reader.AdvanceTo(buff.End);

            context.Next<BasicHeaderProcessor>();
        }
    }
}
