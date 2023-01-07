using System.Runtime.InteropServices;

namespace Spangle.Rtmp.Handshake;


/// <summary>
/// C0 / S0 Message
/// </summary>
/*
  0 1 2 3 4 5 6 7
 +-+-+-+-+-+-+-+-+
 |    version    |
 +-+-+-+-+-+-+-+-+

  C0 and S0 bits 
 */
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1)] 
internal readonly struct C0S0
{
    public readonly RtmpVersion RtmpVersion;

    public C0S0(RtmpVersion version = RtmpVersion.Rtmp3)
    {
        RtmpVersion = version;
    }
}
