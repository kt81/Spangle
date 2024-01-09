using System.Runtime.CompilerServices;

namespace Spangle.Transport.Rtmp.Extensions;

internal static class AmfObjectExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TryCopyTo<T>(this AmfObject anonObject, string key, ref T target)
    {
        if (!anonObject.TryGetValue(key, out object? value)) return;
        if (value is T s)
        {
            target = s;
        }
    }
}
