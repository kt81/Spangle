using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.IO.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Rtmp;

/// <summary>
/// The NetConnection manages a two-way connection between a client application and the server.
/// In addition, it provides support for asynchronous remote method calls.
/// </summary>
internal class NetConnection
{
    private static readonly ILogger<NetConnection> s_logger = SpangleLogManager.GetLogger<NetConnection>();

    public static class Commands
    {
        public const string Connect = "connect";
        public const string Call = "call";
        public const string Close = "close";
        public const string CreateStream = "createStream";
    }

    public static void Connect(
        RtmpReceiverContext context,
        double transactionId,
        IReadOnlyDictionary<string, object> commandObject,
        IReadOnlyDictionary<string, object>? optionalUserArgs = null)
    {
        DumpObject(commandObject);
        DumpObject(optionalUserArgs);

        var band = new BigEndianUInt32 { HostValue = context.Bandwidth };
    }

    public struct ConnectResult
    {
        public string CommandName;
        public double TransactionId;
        public IReadOnlyDictionary<string, object> Properties;
        public IReadOnlyDictionary<string, object> Information;
    }

    [Conditional("DEBUG")]
    private static void DumpObject(IReadOnlyDictionary<string, object>? anonObject,
        [CallerArgumentExpression("anonObject")]
        string? name = null)
    {
        s_logger.ZLogDebug("[{0}]:{1}", name, System.Text.Json.JsonSerializer.Serialize(anonObject));
    }
}
