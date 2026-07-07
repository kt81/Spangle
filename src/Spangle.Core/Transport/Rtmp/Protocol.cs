using Cysharp.Text;

namespace Spangle.Transport.Rtmp;

internal static class Protocol
{
    /// <summary>
    /// Ensures the current message satisfies the constraints of a Protocol Control Message (5.4).
    /// </summary>
    public static void EnsureValidProtocolControlMessage(RtmpReceiverContext context)
    {
        if (context.MessageHeader.StreamId == ControlStreamId)
        {
            return;
        }

        throw new IOException(ZString.Format(
            "Invalid streamId({0}) or chunkStreamId({1}) for Protocol Control Message",
            context.MessageHeader.StreamId, context.BasicHeader.ChunkStreamId));
    }

    /// <summary>
    /// Max chunk size to set
    /// </summary>
    public const uint MaxChunkSize = 65536;

    /// <summary>
    /// Min chunk size to set
    /// </summary>
    public const uint MinChunkSize = 128;

    /// <summary>
    /// Default of max message size
    /// </summary>
    public const uint MaxMessageSizeDefault = MaxChunkSize * 4;

    /*
     * (5.4. Protocol Control Messages)
     * These protocol control messages MUST have message stream ID 0 (known as the control stream)
     * and be sent in chunk stream ID 2.
     * Protocol control messages take effect as soon as they are received;
     * their timestamps are ignored.
     */
    /// <summary>
    /// The stream ID which indicates Control Stream
    /// </summary>
    public const int ControlStreamId = 0;

    /// <summary>
    /// The chunk stream ID which indicates Control Chunk Stream
    /// </summary>
    public const int ControlChunkStreamId = 2;
}
