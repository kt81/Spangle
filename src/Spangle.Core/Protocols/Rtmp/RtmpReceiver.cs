using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Protocols.Rtmp.Handshake;
using ZLogger;

namespace Spangle.Protocols.Rtmp;

public sealed class RtmpReceiver : ReceiverBase<RtmpReceiver, RtmpReceiverContext>
{
    private static readonly ILogger<RtmpReceiver> s_logger;

    static RtmpReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<RtmpReceiver>();
    }

    protected override async ValueTask BeginReadAsync(RtmpReceiverContext context, CancellationTokenSource readTimeoutSource)
    {
        s_logger.ZLogDebug("Begin to handshake");
        await HandshakeHandler.DoHandshakeAsync(context);
        s_logger.ZLogDebug("Handshake done");
        context.ConnectionState = ReceivingState.WaitingConnect;

        while (!context.IsCompleted)
        {
            if (context.Timeout > 0)
            {
                readTimeoutSource.CancelAfter(context.Timeout);
                await context.MoveNext(context);
                readTimeoutSource.TryReset();
            }
            else
            {
                await context.MoveNext(context);
            }
        }

        s_logger.ZLogInformation("Rtmp connection closed");
    }

}
