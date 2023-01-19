using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.IO.Interop;
using Spangle.Logging;
using Spangle.Rtmp.ProtocolControlMessage;
using ZLogger;

namespace Spangle.Rtmp.NetConnection;

/// <summary>
/// The NetConnection manages a two-way connection between a client application and the server.
/// In addition, it provides support for asynchronous remote method calls.
/// </summary>
internal class NetConnection
{
    private static readonly ILogger<NetConnection> s_logger = SpangleLogManager.GetLogger<NetConnection>();

    public static class Commands
    {
        public const string Connect      = "connect";
        public const string Call         = "call";
        public const string Close        = "close";
        public const string CreateStream = "createStream";
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

    public static void Connect(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object?> commandObject,
        IReadOnlyDictionary<string, object?>? optionalUserArgs = null)
    {
        s_logger.ZLogTrace("NetCommand.Connect");
        DumpObject(commandObject);
        DumpObject(optionalUserArgs);

        TryCopy(commandObject, Keys.App, ref context.App);
        TryCopy(commandObject, Keys.SupportsGoAway, ref context.IsGoAwayEnabled);

        var band = new BigEndianUInt32 { HostValue = context.Bandwidth };

        s_logger.ZLogTrace("Send WindowAcknowledgementSize (5)");
        RtmpWriter.Write(context, 0, MessageType.WindowAcknowledgementSize,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref band);

        s_logger.ZLogTrace("Send SetPeerBandwidth (6)");
        var peerBw = new SetPeerBandwidth { AcknowledgementWindowSize = band, LimitType = BandwidthLimitType.Dynamic, };
        RtmpWriter.Write(context, 0, MessageType.SetPeerBandwidth,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref peerBw);

        s_logger.ZLogTrace("Send UseControlMessage (4) StreamBegin (0)");
        var streamBegin = UserControlMessage.Create(UserControlMessageEvents.StreamBegin,
            BigEndianUInt32.FromHost(Protocol.ControlStreamId).ToBytes());
        RtmpWriter.Write(context, 0, MessageType.UserControl,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref streamBegin);

        s_logger.ZLogTrace("Send SetChunkSize (1)");
        // MaxChunkSize's first bit must be 0
        Debug.Assert(context.MaxChunkSize <= 0x7FFF_FFFF);
        var setChunkSize = BigEndianUInt32.FromHost(context.MaxChunkSize);
        RtmpWriter.Write(context, 0, MessageType.SetChunkSize,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref setChunkSize);

        s_logger.ZLogTrace("Send RPC Response _result()");
        var result = new ConnectResult();
        // RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
        //     Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref result);
    }

    [Conditional("DEBUG")]
    private static void DumpObject(IReadOnlyDictionary<string, object?>? anonObject,
        [CallerArgumentExpression("anonObject")]
        string? name = null)
    {
        s_logger.ZLogDebug("[{0}]:{1}", name, System.Text.Json.JsonSerializer.Serialize(anonObject));
    }

    private static void TryCopy<T>(IReadOnlyDictionary<string, object?> anonObject, string key, ref T target)
    {
        if (!anonObject.TryGetValue(key, out object? value)) return;
        if (value is T s)
        {
            target = s;
        }
    }
}
