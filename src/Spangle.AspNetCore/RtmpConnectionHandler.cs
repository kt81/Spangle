using Microsoft.AspNetCore.Connections;
using Spangle.Rtmp;

namespace Spangle.AspNetCore;

public class RtmpConnectionHandler : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        await using var reader = new BufferedStream(connection.Transport.Input.AsStream());
        await using var writer = new BufferedStream(connection.Transport.Output.AsStream());
        var rtmp = new RtmpReceiver(reader, writer);

        await rtmp.BeginReadAsync();
    }
}
