using Microsoft.Extensions.Logging;
using Spangle.Rtmp.Handshake;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Rtmp;

public class RtmpReceiver : IReceiver<RtmpReceiverContext>
{
    private static readonly ILogger<RtmpReceiver> s_logger;

    // TODO 仮置き
    /// <summary>
    /// The stream ID which indicates Control Stream
    /// </summary>
    internal const int ControlStreamId = 0;

    static RtmpReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<RtmpReceiver>();
    }

    public async ValueTask BeginReadAsync(RtmpReceiverContext context)
    {
        var ct = context.CancellationToken;

        s_logger.ZLogDebug("Begin to handshake");
        await HandshakeHandler.DoHandshakeAsync(context);
        s_logger.ZLogDebug("Handshake done");
        context.ConnectionState = ReceivingState.WaitingConnect;

        while (!context.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            await context.MoveNext(context);
        }

        s_logger.ZLogDebug("Begin to read chunk");
    }
}
