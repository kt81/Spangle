using System.Buffers;
using Spangle.Transport.Rtmp.ProtocolControlMessage;

namespace Spangle.Transport.Rtmp.Chunk;

/// <summary>
/// Per-chunk-stream assembly state. RTMP interleaves chunks of different chunk streams
/// (e.g. audio between video chunks), so header state and message assembly must be kept per csid.
/// </summary>
internal sealed class ChunkStreamState(uint chunkStreamId)
{
    public readonly uint ChunkStreamId = chunkStreamId;

    // Values carried over between headers (Fmt1-3 reuse them)
    public uint        MessageLength;
    public MessageType TypeId;
    public uint        MessageStreamId;

    /// <summary>Computed absolute timestamp in milliseconds</summary>
    public uint Timestamp;

    /// <summary>Last timestamp delta; re-applied by a Fmt3 chunk that starts a new message</summary>
    public uint TimestampDelta;

    /// <summary>The last Fmt0-2 header indicated an extended timestamp; Fmt3 chunks then carry it too</summary>
    public bool HasExtendedTimestamp;

    /// <summary>Bytes remaining of the in-flight message; 0 means the next chunk starts a new message</summary>
    public int Remaining;

    /// <summary>Assembled message body</summary>
    public readonly ArrayBufferWriter<byte> Assembly = new(4096);
}
