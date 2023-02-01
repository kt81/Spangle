using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Rtmp.ProtocolControlMessage;
using Spangle.Util;
using ZLogger;

namespace Spangle.Rtmp.NetStream;

/// <summary>
/// The NetStream defines the channel through which the streaming audio, video,
/// and data messages can flow over the NetConnection that connects the client to the server.
/// A NetConnection object can support multiple NetStreams for multiple data streams.
/// </summary>
internal abstract class NetStreamHandler
{
    private static readonly ILogger<NetStreamHandler> s_logger =
        SpangleLogManager.GetLogger<NetStreamHandler>();

    // The following commands can be sent on the NetStream by the client to the server:
    public static class Commands
    {
        public const string Play         = "play";
        public const string Play2        = "play2";
        public const string DeleteStream = "deleteStream";
        public const string CloseStream  = "closeStream";
        public const string ReceiveAudio = "receiveAudio";
        public const string ReceiveVideo = "receiveVideo";
        public const string Publish      = "publish";
        public const string Seek         = "seek";
        public const string Pause        = "pause";
    }

    private static class PublishingTypes
    {
        public const string Live = "live";
        // public const string Record = "record";
        // public const string Append = "append";
    }

    /// <summary>
    /// The client sends the publish command to publish a named stream to the server. Using this name,
    /// any client can play this stream and receive the published audio, video, and data messages.
    /// </summary>
    public static void OnPublish(
        RtmpReceiverContext context,
        double transactionId,
        AmfObject? commandObject,
        string publishingName,
        string publishingType
    )
    {
        if (publishingType != PublishingTypes.Live)
        {
            ThrowHelper.ThrowOverSpec(context);
        }

        context.StreamId = publishingName;
        uint csId = context.BasicHeader.ChunkStreamId;
        const uint publishingStreamId = Protocol.ControlStreamId + 1;

        s_logger.ZLogTrace("Send UserControlMessage (4) StreamBegin (0) : 1");
        var streamBegin = UserControlMessage.Create(UserControlMessageEvents.StreamBegin,
            BigEndianUInt32.FromHost(publishingStreamId).AsSpan());
        RtmpWriter.Write(context, 0, MessageType.UserControl,
            csId, Protocol.ControlStreamId, ref streamBegin);

        var result = new OnStatusResult(OnStatusResult.Level.Status, OnStatusResult.Code.Play,
            $"Stream '{publishingName}' is now published.", publishingName);
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            csId, publishingStreamId, result);
    }
}
