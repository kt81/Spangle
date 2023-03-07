using System.Runtime.InteropServices;

namespace Spangle.Util;

internal readonly struct CoTaskMemDisposerHandle : IDisposable
{
    private readonly ICollection<IntPtr> _handles;

    public CoTaskMemDisposerHandle(params IntPtr[] handles)
    {
        _handles = handles;
    }

    public void Dispose()
    {
        foreach (IntPtr p in _handles)
        {
            Marshal.FreeCoTaskMem(p);
        }
    }
}
