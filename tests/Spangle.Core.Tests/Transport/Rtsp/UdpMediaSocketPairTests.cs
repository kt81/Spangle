using System.Net;
using System.Net.Sockets;
using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

public class UdpMediaSocketPairTests
{
    [Fact]
    public void BindGivesAnEvenRtpPortAndTheNextRtcpPort()
    {
        using UdpMediaSocketPair pair = UdpMediaSocketPair.Bind(IPAddress.Loopback);

        (pair.RtpPort % 2).Should().Be(0, "RTP takes the even port of the pair (RFC 3550)");
        pair.RtcpPort.Should().Be(pair.RtpPort + 1);
        ((IPEndPoint)pair.Rtp.LocalEndPoint!).Port.Should().Be(pair.RtpPort);
        ((IPEndPoint)pair.Rtcp.LocalEndPoint!).Port.Should().Be(pair.RtcpPort);
    }

    [Fact]
    public async Task ReceiveLoopDeliversDatagramsUntilCancelled()
    {
        using UdpMediaSocketPair pair = UdpMediaSocketPair.Bind(IPAddress.Loopback);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var received = new List<byte[]>();
        var gotOne = new TaskCompletionSource();
        Task loop = UdpMediaSocketPair.ReceiveLoopAsync(pair.Rtp, datagram =>
        {
            received.Add(datagram.ToArray());
            gotOne.TrySetResult();
            return ValueTask.CompletedTask;
        }, cts.Token);

        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        byte[] payload = [1, 2, 3, 4, 5];
        await sender.SendToAsync(payload, new IPEndPoint(IPAddress.Loopback, pair.RtpPort), cts.Token);

        await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        received.Should().ContainSingle().Which.Should().Equal(payload);

        await cts.CancelAsync();
        await loop; // the loop ends when the token fires
    }

    [Fact]
    public async Task ReceiveLoopStopsWhenTheSocketIsDisposed()
    {
        UdpMediaSocketPair pair = UdpMediaSocketPair.Bind(IPAddress.Loopback);
        Task loop = UdpMediaSocketPair.ReceiveLoopAsync(pair.Rtp,
            _ => ValueTask.CompletedTask, CancellationToken.None);

        pair.Dispose(); // disposing the socket must end the loop, not hang it

        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
