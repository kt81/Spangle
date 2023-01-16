using System.Runtime.InteropServices;
using Spangle.IO.Interop;
using Spangle.Rtmp.ProtocolControlMessage;

namespace Spangle.Rtmp.Chunk;

/*
  0                   1                   2                   3
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                   timestamp                   |message length |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |     message length (cont)     |message type id| msg stream id |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |           message stream id (cont)            |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

                   Chunk Message Header - Type 0

Type 0 chunk headers are 11 bytes long. This type MUST be used at the start of a chunk stream,
and whenever the stream timestamp goes backward (e.g., because of a backward seek).

----

  0                   1                   2                   3
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                timestamp delta                |message length |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |     message length (cont)     |message type id|
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

                    Chunk Message Header - Type 1

Type 1 chunk headers are 7 bytes long. The message stream ID is not included;
this chunk takes the same stream ID as the preceding chunk.
Streams with variable-sized messages (for example, many video formats) SHOULD use this format
for the first chunk of each new message after the first.

----

  0                   1                   2
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |                timestamp delta                |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

           Chunk Message Header - Type 2

Type 2 chunk headers are 3 bytes long. Neither the stream ID nor the message length is included;
this chunk has the same stream ID and message length as the preceding chunk. Streams with constant-sized messages
(for example, some audio and data formats) SHOULD use this format for the first chunk of each message after the first.

Type3 ... No Header

Type 3 chunks have no message header. The stream ID, message length and timestamp delta fields are not present;
chunks of this type take values from the preceding chunk for the same Chunk Stream ID.
When a single message is split into chunks, all chunks of a message except the first one SHOULD use this type.
Refer to Example 2 (Section 5.3.2.2). A stream consisting of messages of exactly the same size,
stream ID and spacing in time SHOULD use this type for all chunks after a chunk of Type 2.
Refer to Example 1 (Section 5.3.2.1). If the delta between the first message and the second message is
same as the timestamp of the first message, then a chunk of Type 3 could immediately follow the chunk of Type 0
as there is no need for a chunk of Type 2 to register the delta.
If a Type 3 chunk follows a Type 0 chunk, then the timestamp delta for this Type 3 chunk is the same
as the timestamp of the Type 0 chunk.

 */
/// <summary>
/// Chunk Message Header structure
/// Shared by Type0-2 and Type3 with extended timestamp
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = MaxSize)]
internal struct MessageHeader
{
    public const int MaxSize = 27;

    /// <summary>
    /// For a type-0 chunk, the absolute timestamp of the message is sent here.
    /// If the timestamp is greater than or equal to 16777215 (hexadecimal 0xFFFFFF), this field MUST be 16777215,
    /// indicating the presence of the Extended Timestamp field to encode the full 32 bit timestamp.
    /// Otherwise, this field SHOULD be the entire timestamp.
    /// </summary>
    [FieldOffset(0)] public BigEndianUInt24 Timestamp;

    /// <summary>
    /// For a type-1 or type-2 chunk, the difference between the previous chunk’s timestamp and the current chunk’s timestamp is sent here.
    /// If the delta is greater than or equal to 16777215 (hexadecimal 0xFFFFFF), this field MUST be 16777215,
    /// indicating the presence of the Extended Timestamp field to encode the full 32 bit delta.
    /// Otherwise, this field SHOULD be the actual delta.
    /// </summary>
    [FieldOffset(0)] public BigEndianUInt24 TimestampDelta;

    [FieldOffset(3)] public BigEndianUInt24 Length;
    [FieldOffset(6)] public MessageType TypeId;
    [FieldOffset(7)] public uint StreamId;
    [FieldOffset(15)] public BigEndianUInt32 ExtendedTimeStamp;

    /// <summary>
    /// Whether this header has an Extended Timestamp
    /// </summary>
    public bool HasExtendedTimestamp => Timestamp.HostValue == BigEndianUInt24.MaxValue;

    /// <summary>
    /// Irresponsible check of this == default
    /// </summary>
    public bool IsDefault => Length.HostValue == 0;

    public unsafe Span<byte> ToBytes()
    {
        fixed (void* p = &this)
        {
            return new Span<byte>(p, MaxSize);
        }
    }

    public void SetFmt0(uint timestamp, uint length, MessageType typeId, uint streamId)
    {
        if (timestamp > BigEndianUInt24.MaxValue)
        {
            Timestamp = BigEndianUInt24.FromHost(timestamp);
        }
        else
        {
            Timestamp = BigEndianUInt24.FromHost(BigEndianUInt24.MaxValue);
            ExtendedTimeStamp = BigEndianUInt32.FromHost(timestamp);
        }

        Length = BigEndianUInt24.FromHost(length);
        TypeId = typeId;
        StreamId = streamId;
    }

    public void SetFmt1(uint timestampDelta, uint length, MessageType typeId)
    {
        TimestampDelta = BigEndianUInt24.FromHost(timestampDelta);
        Length = BigEndianUInt24.FromHost(length);
        TypeId = typeId;
    }

    public void SetFmt2(uint timestampDelta)
    {
        TimestampDelta = BigEndianUInt24.FromHost(timestampDelta);
    }

}
