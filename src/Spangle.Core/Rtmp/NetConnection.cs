using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Rtmp;

/// <summary>
/// The NetConnection manages a two-way connection between a client application and the server.
/// In addition, it provides support for asynchronous remote method calls.
/// </summary>
internal class NetConnection
{
    private PipeWriter _writer;
    private static ILogger<NetConnection> s_logger = SpangleLogManager.GetLogger<NetConnection>();

    public static class Commands
    {
        public const string Connect = "connect";
        public const string Call = "call";
        public const string Close = "close";
        public const string CreateStream = "createStream";
    }

    public NetConnection(PipeWriter writer)
    {
        _writer = writer;
    }

    public void Connect(double transactionId,
        IReadOnlyDictionary<string, object> commandObject,
        IReadOnlyDictionary<string, object>? optionalUserArgs = null)
    {
        DumpObject(nameof(commandObject), commandObject);
        DumpObject(nameof(optionalUserArgs), optionalUserArgs);
    }

    public struct ConnectResult
    {
        public string CommandName;
        public double TransactionId;
        public IReadOnlyDictionary<string, object> Properties;
        public IReadOnlyDictionary<string, object> Information;
    }

    [Conditional("DEBUG")]
    private static void DumpObject(string name, IReadOnlyDictionary<string, object>? anonymousObject)
    {
        s_logger.ZLogDebug("[{0}]:{1}", name, System.Text.Json.JsonSerializer.Serialize(anonymousObject));
    }
}
