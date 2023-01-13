using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Rtmp.Handshake;
using Spangle.Rtmp.Logging;
using ZLogger;

namespace Spangle.Rtmp;

public class RtmpReceiver : IReceiver<RtmpReceiverContext>
{
    private static readonly ILogger<RtmpReceiver> s_logger;

    static RtmpReceiver()
    {
        s_logger = SpangleLogManager.GetLogger<RtmpReceiver>();
    }

    public async ValueTask BeginReadAsync(RtmpReceiverContext receiverContext)
    {
        s_logger.ZLogDebug("Begin to handshake");
        await HandshakeHandler.DoHandshakeAsync(receiverContext);
        s_logger.ZLogDebug("Handshake done");

        s_logger.ZLogDebug("Begin to read chunk");
    }
}
