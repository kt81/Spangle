using System.Diagnostics.CodeAnalysis;
using Spangle.Rtmp;

namespace Spangle.Util;

public static class ThrowHelper
{
    [DoesNotReturn]
    public static void ThrowOverSpec(IReceiverContext? context = null) =>
        throw new NotInScopeException("🐉 This spec is far beyond my power level...", context);
}
