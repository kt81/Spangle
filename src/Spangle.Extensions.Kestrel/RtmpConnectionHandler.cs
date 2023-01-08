using Microsoft.AspNetCore.Connections;
using Spangle.Rtmp;

namespace Spangle.Extensions.Kestrel;

public class RtmpConnectionHandler : ConnectionHandler
{
    private readonly ILoggerFactory _loggerFactory;
    
    public RtmpConnectionHandler(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }
    
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        await using var rtmp = new RtmpReceiver(connection.Transport, _loggerFactory);
        await rtmp.BeginReadAsync();
    }
}
