using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Rtmp.Extensions;
using Spangle.Rtmp.ProtocolControlMessage;
using ZLogger;

namespace Spangle.Rtmp.NetConnection;

/// <summary>
/// The NetConnection manages a two-way connection between a client application and the server.
/// In addition, it provides support for asynchronous remote method calls.
/// </summary>
internal abstract class NetConnectionHandler
{
    private static readonly ILogger<NetConnectionHandler> s_logger = SpangleLogManager.GetLogger<NetConnectionHandler>();

    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    public static class Commands
    {
        public const string Connect      = "connect";
        public const string Call         = "call";
        public const string Close        = "close";
        public const string CreateStream = "createStream";

        // No document
        public const string ReleaseStream = "releaseStream";
        public const string FCPublish     = "FCPublish";
    }

    private static class Keys
    {
        public const string App            = "app";
        public const string Type           = "type";
        public const string SupportsGoAway = "supportsGoAway";
        public const string FlashVer       = "flashVer";
        public const string SwfUrl         = "swfUrl";
        public const string TcUrl          = "tcUrl";
    }

    public static void OnConnect(
        RtmpReceiverContext context,
        double transactionId,
        AmfObject commandObject,
        AmfObject? optionalUserArgs = null)
    {
        s_logger.ZLogTrace("NetCommand.Connect");
        s_logger.DumpObject(commandObject);
        s_logger.DumpObject(optionalUserArgs);

        commandObject.TryCopyTo(Keys.App, ref context.App);
        commandObject.TryCopyTo(Keys.SupportsGoAway, ref context.IsGoAwayEnabled);

        var band = BigEndianUInt32.FromHost(context.Bandwidth);

        s_logger.ZLogTrace("Send WindowAcknowledgementSize (5)");
        RtmpWriter.Write(context, 0, MessageType.WindowAcknowledgementSize,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref band);

        s_logger.ZLogTrace("Send SetPeerBandwidth (6)");
        var peerBw = new SetPeerBandwidth { AcknowledgementWindowSize = band, LimitType = BandwidthLimitType.Dynamic, };
        RtmpWriter.Write(context, 0, MessageType.SetPeerBandwidth,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref peerBw);

        s_logger.ZLogTrace("Send UseControlMessage (4) StreamBegin (0) : 0");
        var streamBegin = UserControlMessage.Create(UserControlMessageEvents.StreamBegin,
            BigEndianUInt32.FromHost(Protocol.ControlStreamId).AsSpan());
        RtmpWriter.Write(context, 0, MessageType.UserControl,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref streamBegin);

        s_logger.ZLogTrace("Send SetChunkSize (1)");
        // MaxChunkSize's first bit must be 0
        Debug.Assert(context.MaxChunkSize <= 0x7FFF_FFFF);
        var setChunkSize = BigEndianUInt32.FromHost(context.MaxChunkSize);
        RtmpWriter.Write(context, 0, MessageType.SetChunkSize,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref setChunkSize);

        s_logger.ZLogTrace("Send RPC Response _result() for {0}()", nameof(OnConnect));
        var result = ConnectResult.CreateDefault();
        result.TransactionId = transactionId; // expected always 1
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            context.BasicHeader.ChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void OnReleaseStream(
        RtmpReceiverContext context,
        double transactionId,
        AmfObject? commandObject,
        string streamName)
    {
        s_logger.ZLogTrace("Send _result ({0}, {1}, {2})", transactionId, streamName, nameof(OnReleaseStream));
        context.PreparingStreamName = streamName;
        var result = new CommonResult { CommandName = "_result", TransactionId = transactionId, Properties = null, };
        context.ReleaseStream(streamName);
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void OnFCPublish(
        RtmpReceiverContext context,
        double transactionId,
        AmfObject? commandObject,
        string streamName)
    {
        s_logger.ZLogTrace("Send onFCPublish ({0}, {1})", transactionId, streamName);
        context.PreparingStreamName = streamName;
        var result = new CommonResult
        {
            CommandName = "onFCPublish", TransactionId = transactionId, Properties = null,
        };
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void OnCreateStream(
        RtmpReceiverContext context,
        double transactionId,
        AmfObject? commandObject)
    {
        if (context.PreparingStreamName is null)
        {
            context.PreparingStreamName = "none-fcPublish-ph";
        }

        var stream = context.CreateStream(context.PreparingStreamName);
        s_logger.ZLogTrace("Created stream: {0}({1})", stream.Id, stream.Name);

        s_logger.ZLogTrace("Send RPC Response _result() for {0}()", nameof(OnCreateStream));
        var result = CreateStreamResult.Create(transactionId, stream.Id);
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }
}
