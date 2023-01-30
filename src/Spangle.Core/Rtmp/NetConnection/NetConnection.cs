﻿using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
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

    public static void Connect(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object?> commandObject,
        IReadOnlyDictionary<string, object?>? optionalUserArgs = null)
    {
        s_logger.ZLogTrace("NetCommand.Connect");
        DumpObject(commandObject);
        DumpObject(optionalUserArgs);

        TryCopyFromAnonObj(commandObject, Keys.App, ref context.App);
        TryCopyFromAnonObj(commandObject, Keys.SupportsGoAway, ref context.IsGoAwayEnabled);

        var band = BigEndianUInt32.FromHost(context.Bandwidth);

        s_logger.ZLogTrace("Send WindowAcknowledgementSize (5)");
        RtmpWriter.Write(context, 0, MessageType.WindowAcknowledgementSize,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref band);

        s_logger.ZLogTrace("Send SetPeerBandwidth (6)");
        var peerBw = new SetPeerBandwidth { AcknowledgementWindowSize = band, LimitType = BandwidthLimitType.Dynamic, };
        RtmpWriter.Write(context, 0, MessageType.SetPeerBandwidth,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref peerBw);

        s_logger.ZLogTrace("Send UseControlMessage (4) StreamBegin (0)");
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

        s_logger.ZLogTrace("Send RPC Response _result() for {0}()", nameof(Connect));
        var result = ConnectResult.CreateDefault();
        result.TransactionId = transactionId; // expected always 1
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void ReleaseStream(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object?>? commandObject,
        string streamId)
    {
        s_logger.ZLogTrace("Send _result ({0}, {1}, {2})", transactionId, streamId, nameof(ReleaseStream));
        context.StreamId = streamId;
        var result = new CommonResult { CommandName = "_result", TransactionId = transactionId, Properties = null, };
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void FCPublish(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object?>? commandObject,
        string streamId)
    {
        s_logger.ZLogTrace("Send onFCPublish ({0}, {1})", transactionId, streamId);
        context.StreamId = streamId;
        var result = new CommonResult
        {
            CommandName = "onFCPublish", TransactionId = transactionId, Properties = null,
        };
        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    public static void CreateStream(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object?>? commandObject)
    {
        s_logger.ZLogTrace("Send RPC Response _result() for {0}()", nameof(CreateStream));
        var result = ConnectResult.CreateDefault();
        result.TransactionId = transactionId;

        RtmpWriter.Write(context, 0, MessageType.CommandAmf0,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, result);
    }

    [Conditional("DEBUG")]
    private static void DumpObject(IReadOnlyDictionary<string, object?>? anonObject,
        [CallerArgumentExpression("anonObject")]
        string? name = null)
    {
        s_logger.ZLogDebug("[{0}]:{1}", name, System.Text.Json.JsonSerializer.Serialize(anonObject));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryCopyFromAnonObj<T>(IReadOnlyDictionary<string, object?> anonObject, string key, ref T target)
    {
        if (!anonObject.TryGetValue(key, out object? value)) return;
        if (value is T s)
        {
            target = s;
        }
    }
}
