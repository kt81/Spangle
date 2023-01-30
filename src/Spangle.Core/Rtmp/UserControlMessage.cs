using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Rtmp;

[StructLayout(LayoutKind.Sequential, Pack = 2, Size = Size)]
internal unsafe struct UserControlMessage
{
    public const  int Size               = 6;
    private const int EventDataMaxLength = 4;

    public       BigEndianUInt16 EventType;
    public fixed byte            EventData[EventDataMaxLength];

    public static UserControlMessage Create(UserControlMessageEvents type, ReadOnlySpan<byte> data)
    {
        var self = new UserControlMessage { EventType = BigEndianUInt16.FromHost((ushort)type) };
        data.CopyTo(new Span<byte>(self.EventData, EventDataMaxLength));
        return self;
    }
}

internal enum UserControlMessageEvents : ushort
{
    /// <summary>
    /// The server sends this event to notify the client
    /// that a stream has become functional and can be
    /// used for communication. By default, this event
    /// is sent on ID 0 after the application connect
    /// command is successfully received from the
    /// client. The event data is 4-byte and represents
    /// the stream ID of the stream that became
    /// functional.
    /// </summary>
    StreamBegin = 0,

    /// <summary>
    /// The server sends this event to notify the client
    /// that the playback of data is over as requested
    /// on this stream. No more data is sent without
    /// issuing additional commands. The client discards
    /// the messages received for the stream. The
    /// 4 bytes of event data represent the ID of the
    /// stream on which playback has ended.
    /// </summary>
    StreamEOF = 1,

    /// <summary>
    /// The server sends this event to notify the client
    /// that there is no more data on the stream. If the
    /// server does not detect any message for a time
    /// period, it can notify the subscribed clients
    /// that the stream is dry. The 4 bytes of event
    /// data represent the stream ID of the dry stream.
    /// </summary>
    StreamDry = 2,

    /// <summary>
    /// The client sends this event to inform the server
    /// of the buffer size (in milliseconds) that is
    /// used to buffer any data coming over a stream.
    /// This event is sent before the server starts
    /// processing the stream. The first 4 bytes of the
    /// event data represent the stream ID and the next
    /// 4 bytes represent the buffer length, in
    /// milliseconds.
    /// </summary>
    SetBufferLength = 3,

    /// <summary>
    /// The server sends this event to notify the client
    /// that the stream is a recorded stream. The
    /// 4 bytes event data represent the stream ID of
    /// the recorded stream.
    /// </summary>
    StreamIsRecorded = 4,

    /// <summary>
    /// The server sends this event to test whether the
    /// client is reachable. Event data is a 4-byte
    /// timestamp, representing the local server time
    /// when the server dispatched the command. The
    /// client responds with PingResponse on receiving
    /// MsgPingRequest.
    /// </summary>
    PingRequest = 6,

    /// <summary>
    /// The client sends this event to the server in
    /// response to the ping request. The event data is
    /// a 4-byte timestamp, which was received with the
    /// PingRequest request.
    /// </summary>
    PingResponse = 7,
}
