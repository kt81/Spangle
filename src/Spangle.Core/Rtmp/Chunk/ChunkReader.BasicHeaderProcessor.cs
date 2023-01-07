using System.Buffers;
using Spangle.IO;

namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    /// <summary>
    /// BasicHeaderProcessor
    /// <see cref="ChunkBasicHeader"/>
    /// </summary>
    private class BasicHeaderProcessor : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var reader = context._reader;
            
            // Check first byte 
            var (firstBuff, _) = await reader.ReadExactlyAsync(1, ct);
            var (fmt, headerLength, checkBits) = ChunkBasicHeader.GetFormatAndLengthByFirstByte(firstBuff.FirstSpan[0]);
            reader.AdvanceTo(firstBuff.End);

            uint csId;
            // Read the remaining buffer if needed
            if (headerLength > 1)
            {
                var (exBuff, _) = await reader.ReadExactlyAsync(headerLength - 1, ct);
                csId = GetCsId(exBuff, headerLength);
                reader.AdvanceTo(exBuff.End);
            }
            else
            {
                // 1 byte version
                csId = checkBits;
            }

            context._chunk.BasicHeader.Renew(fmt, csId);
            context.Next<MessageHeaderProcessor>();
        }

        private static uint GetCsId(ReadOnlySequence<byte> buff, int headerLength)
        {
            var csIdBytes = buff.FirstSpan;
            return headerLength switch
            {
                // 3-bytes version
                3 => (uint)(csIdBytes[0] << 8) + csIdBytes[1] + 64,
                // 2-bytes version
                2 => (uint)(csIdBytes[0] + 64),
                // not reachable
                _ => throw new Exception(),
            };
        }
    }
}
