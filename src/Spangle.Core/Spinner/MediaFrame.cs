using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Spinner;

public enum MediaFrameKind : byte
{
    Video = 0,
    Audio = 1,

    /// <summary>Timed metadata (see <see cref="DataCodec"/>); synchronized to the media timeline</summary>
    Data = 2,
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
/// <para>
/// Timestamps are <b>90 kHz ticks</b> — the MPEG-2 systems / RTP video clock. Every source
/// normalizes onto it at ingest (RTMP milliseconds × 90, MPEG-TS PES verbatim, RTP video
/// verbatim, LOC microseconds × 9 / 100) and every sink reads it back out, so the TS and CMAF
/// paths — already 90 kHz internally — no longer round-trip through milliseconds. The field is
/// 64-bit: a 90 kHz tick counter takes ~3.25 million years to overflow, so the timeline never
/// wraps within the process.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public struct MediaFrameHeader
{
    public const int Size = 28;

    /// <summary>
    /// Sync marker at the front of every header ('SPRK'). The pipe never desynchronizes in normal
    /// operation, but a bug that miscounts a payload length would otherwise be read as silently
    /// garbled frames; a mismatch here turns that into a loud <see cref="InvalidDataException"/>.
    /// </summary>
    public const uint MagicValue = 0x5350524B; // 'S' 'P' 'R' 'K' — Spangle spark

    public uint            Magic;
    public MediaFrameKind  Kind;
    public MediaFrameFlags Flags;
    private ushort         _reserved;

    /// <summary>The codec of the payload; a <see cref="VideoCodec"/> or <see cref="AudioCodec"/> value depending on <see cref="Kind"/></summary>
    public uint Codec;

    /// <summary>PTS minus DTS in 90 kHz ticks (video with B-frames); 0 otherwise</summary>
    public int CompositionTime;

    /// <summary>Payload length in bytes (not including this header)</summary>
    public int Length;

    /// <summary>Decoding timestamp in 90 kHz ticks</summary>
    public long Timestamp;

    public readonly bool IsKeyFrame => (Flags & MediaFrameFlags.KeyFrame) != 0;
    public readonly bool IsConfig => (Flags & MediaFrameFlags.Config) != 0;
    public readonly bool IsValid => Magic == MagicValue;

    public readonly VideoCodec VideoCodec => (VideoCodec)Codec;
    public readonly AudioCodec AudioCodec => (AudioCodec)Codec;
    public readonly DataCodec DataCodec => (DataCodec)Codec;

    public static void Write(PipeWriter writer, MediaFrameKind kind, MediaFrameFlags flags, uint codec,
        int compositionTime, int length, long timestamp)
    {
        var header = new MediaFrameHeader
        {
            Magic = MagicValue,
            Kind = kind,
            Flags = flags,
            Codec = codec,
            CompositionTime = compositionTime,
            Length = length,
            Timestamp = timestamp,
        };
        var span = writer.GetSpan(Size);
        MemoryMarshal.Write(span, in header);
        writer.Advance(Size);
    }

    /// <summary>
    /// Reads a header off the front of <paramref name="source"/> (which must be exactly
    /// <see cref="Size"/> bytes), verifying the sync marker. A mismatch means the pipe is
    /// misframed — the only honest response is to fail loudly rather than decode noise.
    /// </summary>
    public static MediaFrameHeader Read(in ReadOnlySequence<byte> source)
    {
        MediaFrameHeader header = BufferMarshal.AsRefOrCopy<MediaFrameHeader>(source);
        if (!header.IsValid)
        {
            throw new InvalidDataException(
                $"Media frame pipe out of sync: header magic was 0x{header.Magic:X8}, expected 0x{MagicValue:X8}");
        }

        return header;
    }
}
