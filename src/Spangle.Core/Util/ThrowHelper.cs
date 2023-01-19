using System.Diagnostics.CodeAnalysis;

namespace Spangle.Util;

public static class ThrowHelper
{
    [DoesNotReturn]
    public static void ThrowOverSpec() =>
        throw new NotSupportedException("🐉 This spec is far beyond my power level...");
}
