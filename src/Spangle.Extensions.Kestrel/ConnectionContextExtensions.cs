using Microsoft.AspNetCore.Connections;
using Spangle.Transport.Rtmp;

namespace Spangle.Extensions.Kestrel;

public static class ConnectionContextExtensions
{
    public static RtmpReceiverContext CreateRtmpReceiverContext(this ConnectionContext connectionContext, CancellationToken ct)
    {
        return new RtmpReceiverContext(
            connectionContext.Transport.Input,
            connectionContext.Transport.Output,
            connectionContext.RemoteEndPoint!,
            ct,
            connectionContext.ConnectionId
        );

    }
}
