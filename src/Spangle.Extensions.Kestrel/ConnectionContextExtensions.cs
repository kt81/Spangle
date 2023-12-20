using Cysharp.Text;
using Microsoft.AspNetCore.Connections;
using Spangle.Protocols.Rtmp;

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
