using Microsoft.AspNetCore.Connections;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtmp;

namespace Spangle.Extensions.Kestrel;

public class RtmpConnectionHandler : ConnectionHandler
{
    public override Task OnConnectedAsync(ConnectionContext connection)
    {
        var receiver = connection.CreateRtmpReceiverContext(default);
        var live = new LiveContext(receiver, new HLSSenderContext(default));
        return live.StartAsync().AsTask();
    }
}
