namespace Spangle.Rtmp.Chunk;

internal partial class ChunkReader
{
    private class BasicHeaderProcessor : IChunkProcessor
    {
        public async ValueTask ReadAndNext(ChunkReader context, CancellationToken ct)
        {
            var stream = context._reader;
            var buff = context._chunkBuffer;
            var idx = 0;
            
            await stream.ReadExactlyAsync(buff.AsMemory(idx, 1), ct);
        
            var fmt = (byte)(buff[idx] >>> 6);
            var checkBits = (uint)buff[idx] & 0b0011_1111;

            idx++;
            uint csId;
            switch (checkBits)
            {
                case 1:
                    // 3-byte version
                    await stream.ReadExactlyAsync(buff.AsMemory(idx, 2), ct);
                    csId = (uint)buff[idx] << 8 + buff[idx+1] + 64;
                    break;
                case 0:
                    // 2-byte version
                    await stream.ReadExactlyAsync(buff.AsMemory(idx, 1), ct);
                    csId = (uint)buff[idx] + 64;
                    break;
                default:
                    // 1-byte version
                    csId = checkBits;
                    break;
            }

            context._chunk.BasicHeader.Renew(fmt, csId);
            context.Next<MessageHeaderProcessor>();
        }
    }
}
