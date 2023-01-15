using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Spangle.Util;

[SuppressMessage("ReSharper", "StaticMemberInGenericType")]
public static class MarshalHelper<T> where T : unmanaged
{
    public static readonly int Size;

    static MarshalHelper()
    {
        Size = Marshal.SizeOf<T>();
    }
}
