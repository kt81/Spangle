using System.Net;
using System.Net.Sockets;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// A bound RTP/RTCP UDP socket pair for one track: RTP on an even port and RTCP on the
/// next (odd) port, per RFC 3550 §11. Used by both the pull receiver (its own
/// <c>client_port</c>) and the push server (its <c>server_port</c>).
/// </summary>
internal sealed class UdpMediaSocketPair : IDisposable
{
    public Socket Rtp { get; }
    public Socket Rtcp { get; }
    public int RtpPort { get; }
    public int RtcpPort { get; }

    private UdpMediaSocketPair(Socket rtp, Socket rtcp, int rtpPort, int rtcpPort)
    {
        Rtp = rtp;
        Rtcp = rtcp;
        RtpPort = rtpPort;
        RtcpPort = rtcpPort;
    }

    /// <summary>Binds an even RTP port with RTCP on the following port, retrying on collisions.</summary>
    public static UdpMediaSocketPair Bind(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var rtp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                rtp.Bind(new IPEndPoint(address, 0));
                int rtpPort = ((IPEndPoint)rtp.LocalEndPoint!).Port;
                if (rtpPort % 2 != 0)
                {
                    rtp.Dispose(); // RTP must be the even port of the pair
                    continue;
                }
                var rtcp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    rtcp.Bind(new IPEndPoint(address, rtpPort + 1));
                    return new UdpMediaSocketPair(rtp, rtcp, rtpPort, rtpPort + 1);
                }
                catch (SocketException)
                {
                    rtcp.Dispose();
                    rtp.Dispose(); // the odd port was taken; start over
                }
            }
            catch (SocketException)
            {
                rtp.Dispose();
            }
        }
        throw new IOException("Could not bind an RTP/RTCP UDP socket pair after repeated attempts");
    }

    /// <summary>
    /// Reads datagrams from <paramref name="socket"/> until the token fires, handing each to
    /// <paramref name="onDatagram"/> synchronously (the buffer is reused between reads).
    /// </summary>
    public static async Task ReceiveLoopAsync(Socket socket, Func<ReadOnlyMemory<byte>, ValueTask> onDatagram,
        CancellationToken ct)
    {
        var buffer = new byte[65536]; // a UDP datagram never exceeds this
        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
            if (received > 0)
            {
                await onDatagram(new ReadOnlyMemory<byte>(buffer, 0, received)).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        Rtp.Dispose();
        Rtcp.Dispose();
    }
}

/// <summary>How RTP/RTCP are carried: interleaved in the RTSP connection, or on UDP sockets.</summary>
public enum RtspTransportMode
{
    /// <summary>RTP/RTCP framed inside the RTSP TCP connection (RFC 2326 §10.12); the robust default.</summary>
    Tcp,

    /// <summary>RTP/RTCP on their own UDP sockets (lower latency; sensitive to loss/reordering).</summary>
    Udp,
}
