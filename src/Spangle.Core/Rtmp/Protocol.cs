namespace Spangle.Rtmp;

internal static class Protocol
{
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
    /// The stream ID which indicates Control Chunk Stream
    /// </summary>
    public const int ControlChunkStreamId = 2;
}
