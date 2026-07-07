using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace Spangle.Spinner;

public enum MediaFrameKind : byte
{
    Video = 0,
    Audio = 1,
}

[Flags]
public enum MediaFrameFlags : byte
{
    None     = 0,
    KeyFrame = 1,
}

/// <summary>
/// In-band header written into the media pipe before each frame payload.
/// Receiver states write [header][payload] sequences; a spinner consumes them in arrival order.
/// Values are host-endian: this never leaves the process.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public struct MediaFrameHeader
{
    public const int Size = 12;

    public MediaFrameKind  Kind;
    public MediaFrameFlags Flags;
    private ushort         _reserved;

    /// <summary>Payload length in bytes (not including this header)</summary>
    public int Length;

    /// <summary>Timestamp in milliseconds</summary>
    public uint Timestamp;

    public readonly bool IsKeyFrame => (Flags & MediaFrameFlags.KeyFrame) != 0;

    public static void Write(PipeWriter writer, MediaFrameKind kind, MediaFrameFlags flags, int length, uint timestamp)
    {
        var header = new MediaFrameHeader
        {
            Kind = kind,
            Flags = flags,
            Length = length,
            Timestamp = timestamp,
        };
        var span = writer.GetSpan(Size);
        MemoryMarshal.Write(span, in header);
        writer.Advance(Size);
    }
}
