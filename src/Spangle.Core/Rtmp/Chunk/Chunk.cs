namespace Spangle.Rtmp.Chunk;

/*
 +--------------+----------------+--------------------+--------------+
 | Basic Header | Message Header | Extended Timestamp |  Chunk Data  |
 +--------------+----------------+--------------------+--------------+
 |                                                    |
 |<------------------- Chunk Header ----------------->|

                                Chunk Format
 */
internal struct Chunk
{
    public const int MaxSize = 16777215; // SPEC
    
    public ChunkBasicHeader BasicHeader;
    public ChunkMessageHeader MessageHeader;
    public byte[] Body;
}
