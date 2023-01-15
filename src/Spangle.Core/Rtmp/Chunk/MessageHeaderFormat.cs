using System.ComponentModel;

namespace Spangle.Rtmp.Chunk;

internal enum MessageHeaderFormat : byte
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

internal static class MessageHeaderFormatExtensions
{
    public static int GetMessageHeaderLength(this MessageHeaderFormat fmt)
    {
        return fmt switch
        {
            MessageHeaderFormat.Fmt0 => 11,
            MessageHeaderFormat.Fmt1 => 7,
            MessageHeaderFormat.Fmt2 => 3,
            MessageHeaderFormat.Fmt3 => 0,
            _ => throw new InvalidEnumArgumentException()
        };
    }
}
