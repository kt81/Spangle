namespace Spangle.Rtmp.Chunk;

/// <summary>
/// Chunk Basic Header
/// 1 ～ 3 bytes
/// </summary>
/*
  0 1 2 3 4 5 6 7
 +-+-+-+-+-+-+-+-+
 |fmt|   cs id   |
 +-+-+-+-+-+-+-+-+

 Chunk basic header 1

----

  0                   1
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     0     |   cs id - 64  |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 2

----

  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     1     |          cs id - 64           |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 3
 */
internal struct ChunkBasicHeader
{
    public ChunkFormat Format;
    public uint        ChunkStreamId;

    public void Renew(byte format, uint chunkStreamId)
    {
        Format = (ChunkFormat)format;
        ChunkStreamId = chunkStreamId;
    }

    public static (byte Fmt, int BasicHeaderLength, byte CheckBits) GetFormatAndLengthByFirstByte(byte firstByte)
    {
        var fmt = (byte)(firstByte >>> 6);
        var checkBits = (byte)(firstByte & 0b0011_1111);
        int length = checkBits switch
        {
            1 => 3,
            0 => 2,
            _ => 1,
        };

        return (fmt, length, checkBits);
    }
}
