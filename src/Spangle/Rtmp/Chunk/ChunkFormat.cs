using System.ComponentModel;

namespace Spangle.Rtmp.Chunk;

internal enum ChunkFormat : byte
{
    /// <summary>
    /// 11 bytes
    /// </summary>
    Fmt0 = 0,

    /// <summary>
    /// 7 bytes
    /// </summary>
    Fmt1 = 1,

    /// <summary>
    /// 3 bytes
    /// </summary>
    Fmt2 = 2,
    
    /// <summary>
    /// 0 byte
    /// </summary>
    Fmt3 = 3,
}

internal static class ChunkFormatExtension
{
    public static int GetLength(this ChunkFormat fmt)
    {
        return fmt switch
        {
            ChunkFormat.Fmt0 => 11,
            ChunkFormat.Fmt1 => 7,
            ChunkFormat.Fmt2 => 3,
            ChunkFormat.Fmt3 => 0,
            _ => throw new InvalidEnumArgumentException()
        };
    }
}
