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
    None = 0,

    /// <summary>The frame is a random access point (video keyframe)</summary>
    KeyFrame = 1,

    /// <summary>The payload is codec configuration (e.g. AVCC/HVCC record, AudioSpecificConfig), not coded media</summary>
    Config = 2,
}

/// <summary>
/// In-band header written into the media pipe before each frame payload.
/// <para>
/// This is the boundary between transport and processing: the receiver (e.g. RTMP) unwraps its
/// envelope (FLV tags) completely and emits self-contained frames, so downstream spinners only
/// need to understand the codec payload itself.
/// Frames are written as [header][payload] sequences in arrival order.
/// Values are host-endian: this never leaves the process.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public struct MediaFrameHeader
{
    public const int Size = 20;

    public MediaFrameKind  Kind;
    public MediaFrameFlags Flags;
    private ushort         _reserved;

    /// <summary>The codec of the payload; a <see cref="VideoCodec"/> or <see cref="AudioCodec"/> value depending on <see cref="Kind"/></summary>
    public uint Codec;

    /// <summary>PTS minus DTS in milliseconds (video with B-frames); 0 otherwise</summary>
    public int CompositionTimeMs;

    /// <summary>Payload length in bytes (not including this header)</summary>
    public int Length;

    /// <summary>Decoding timestamp in milliseconds</summary>
    public uint Timestamp;

    public readonly bool IsKeyFrame => (Flags & MediaFrameFlags.KeyFrame) != 0;
    public readonly bool IsConfig => (Flags & MediaFrameFlags.Config) != 0;

    public readonly VideoCodec VideoCodec => (VideoCodec)Codec;
    public readonly AudioCodec AudioCodec => (AudioCodec)Codec;

    public static void Write(PipeWriter writer, MediaFrameKind kind, MediaFrameFlags flags, uint codec,
        int compositionTimeMs, int length, uint timestamp)
    {
        var header = new MediaFrameHeader
        {
            Kind = kind,
            Flags = flags,
            Codec = codec,
            CompositionTimeMs = compositionTimeMs,
            Length = length,
            Timestamp = timestamp,
        };
        var span = writer.GetSpan(Size);
        MemoryMarshal.Write(span, in header);
        writer.Advance(Size);
    }
}
