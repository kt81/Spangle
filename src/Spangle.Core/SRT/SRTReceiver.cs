using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Rtmp.Handshake;
using Spangle.Logging;
using ZLogger;

namespace Spangle.SRT;

public sealed class SRTReceiver : ReceiverBase<SRTReceiver, SRTReceiverContext>
{
    private static readonly ILogger<SRTReceiver> s_logger;

    private bool _disposed = false;

    static SRTReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<SRTReceiver>();
    }

    protected override async ValueTask BeginReadAsync(SRTReceiverContext context,
        CancellationTokenSource readTimeoutSource)
    {
        s_logger.ZLogDebug("Begin to handshake");
        s_logger.ZLogDebug("Handshake done");

        while (!context.IsCompleted)
        {
            var result = await context.Reader.ReadAsync(readTimeoutSource.Token);
            s_logger.ZLogDebug("Data received");
            DumpHex(result.Buffer.ToArray(), s_logger.ZLogDebug);
            context.Reader.AdvanceTo(result.Buffer.End);
            // if (context.Timeout > 0)
            // {
            //     readTimeoutSource.CancelAfter(context.Timeout);
            //     await context.MoveNext(context);
            //     readTimeoutSource.TryReset();
            // }
            // else
            // {
            //     await context.MoveNext(context);
            // }

        }

        s_logger.ZLogInformation("SRT connection closed");
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed) // DO NOT invert to "early return"
        {
            if (disposing)
            {
                // place holder
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    ~SRTReceiver() => Dispose(false);

    public static void DumpHex(ReadOnlySpan<byte> data, Action<string> output)
    {
        const int tokenLen = 8;
        const int lineTokensLen = 8;
        while (data.Length > 0)
        {
            var lineTokens = new string[lineTokensLen];
            for (var i = 0; i < lineTokensLen && data.Length > 0; i++)
            {
                int len = Math.Min(tokenLen, data.Length);
                var token = data[..len];
                lineTokens[i] = string.Join(' ', token.ToArray().Select(x => $"{x:X02}"));
                data = data[len..];
            }
            output.Invoke(string.Join("  ", lineTokens));
        }
    }
}
