using SrtSharp;

namespace Spangle.Net.Transport.SRT;

public static class ThrowHelper
{
    public static void ThrowIfError(int handle)
    {
        if (handle < 0)
        {
            throw new SRTException(srt.srt_getlasterror_str());
        }
    }

}
