using Microsoft.AspNetCore.Connections;
using Spangle.Transport.Rtmp;

namespace Spangle.Extensions.Kestrel;

public class RtmpConnectionHandler : ConnectionHandler
{
    private readonly RtmpReceiver _rtmp;

    public RtmpConnectionHandler(RtmpReceiver rtmp)
    {
        _rtmp = rtmp;
    }

    public override Task OnConnectedAsync(ConnectionContext connection)
    {
        var context = connection.CreateRtmpReceiverContext(default);
        return _rtmp.StartAsync(context).AsTask();
    }
}
