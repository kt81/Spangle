using System.Net;
using Spangle.Transport.Rtsp.Sdp;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// One UDP-transported track: the local RTP/RTCP sockets SETUP bound and the remote
/// endpoints to NAT-punch and receive from. The receiver owns the lifetime and disposes it.
/// </summary>
internal sealed class UdpTrackBinding(
    SdpMediaKind kind, UdpMediaSocketPair sockets, IPEndPoint serverRtp, IPEndPoint serverRtcp) : IDisposable
{
    public SdpMediaKind Kind => kind;
    public UdpMediaSocketPair Sockets => sockets;
    public IPEndPoint ServerRtp => serverRtp;
    public IPEndPoint ServerRtcp => serverRtcp;

    public void Dispose() => sockets.Dispose();
}
