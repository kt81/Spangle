using System.Diagnostics.CodeAnalysis;
using Spangle.Transport.Rtmp;

namespace Spangle.Util;

public static class ThrowHelper
{
    [DoesNotReturn]
    public static void ThrowOverSpec(IReceiverContext? context = null, string? additionalMessage = null)
    {
        var msg = "🐉 This spec is far beyond my power level...";
        if (additionalMessage != null)
        {
            msg += $" ({additionalMessage})";
        }
        throw new NotInScopeException(msg, context);
    }
}
