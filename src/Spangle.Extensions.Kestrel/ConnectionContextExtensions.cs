using Cysharp.Text;
using Microsoft.AspNetCore.Connections;

namespace Spangle.Extensions.Kestrel;

public static class ConnectionContextExtensions
{
    public static string GetIdentifier(this ConnectionContext context)
    {
        return ZString.Concat(context.ConnectionId, "[", context.RemoteEndPoint?.ToString() ?? "none", "]");
    }

}
