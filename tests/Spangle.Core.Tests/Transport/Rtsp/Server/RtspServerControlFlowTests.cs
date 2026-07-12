using System.IO.Pipelines;
using System.Net;
using System.Text;
using Spangle.Transport.Rtsp;
using Spangle.Transport.Rtsp.Sdp;
using Spangle.Transport.Rtsp.Server;

namespace Spangle.Tests.Transport.Rtsp.Server;

/// <summary>
/// Drives the RTSP publish handshake state machine (<see cref="RtspServerControlFlow"/>)
/// directly, one request at a time: the ffmpeg-style OPTIONS → ANNOUNCE → SETUP → RECORD
/// flow this server answers when a client pushes a stream to it, plus the rejections
/// (unsupported codec, non-TCP transport, unknown method) it must produce.
/// </summary>
public class RtspServerControlFlowTests
{
    private const byte VideoPayloadType = 96;

    // A minimal but valid H.264 baseline SPS/PPS; TS output does not need the dimensions.
    private static readonly byte[] s_sps = [0x67, 0x42, 0x00, 0x0A, 0xF8, 0x41, 0xA2];
    private static readonly byte[] s_pps = [0x68, 0xCE, 0x38, 0x80];

    [Fact]
    public async Task Options_AdvertisesPublishMethods()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(
            Request("OPTIONS rtsp://127.0.0.1/live/cam RTSP/1.0\r\nCSeq: 1\r\n\r\n"));

        response.StatusCode.Should().Be(200);
        response.Headers.Should().ContainKey("Public");
        response.Headers["Public"].Should().Contain("ANNOUNCE").And.Contain("RECORD");
    }

    [Fact]
    public async Task Announce_WithSupportedVideo_AcceptsAndNamesTheStream()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(BuildAnnounce("rtsp://127.0.0.1/live/cam", VideoSdp()));

        response.StatusCode.Should().Be(200);
        response.Headers.Should().ContainKey("Session");
        flow.StreamName.Should().Be("cam", "the publish key is the ANNOUNCE URL's last path segment");

        // The wired track can then be addressed by SETUP: the channel map fills in.
        RtspResponse setup = await flow.HandleAsync(BuildSetup(
            "rtsp://127.0.0.1/live/cam/streamid=0", "RTP/AVP/TCP;unicast;interleaved=0-1;mode=record"));
        setup.StatusCode.Should().Be(200);
        flow.Channels.Should().ContainKey(0);
    }

    [Fact]
    public async Task Announce_WithOnlyUnsupportedCodec_Returns415()
    {
        RtspServerControlFlow flow = NewFlow();

        // VP8 is not wired by the adapter, so no usable track is described.
        const string sdp =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=control:streamid=0\r\n";

        RtspResponse response = await flow.HandleAsync(BuildAnnounce("rtsp://127.0.0.1/live/cam", sdp));

        response.StatusCode.Should().Be(415);
    }

    [Fact]
    public async Task Setup_WithInterleavedTcp_MapsBothChannelsToTheVideoTrack()
    {
        RtspServerControlFlow flow = NewFlow();
        await flow.HandleAsync(BuildAnnounce("rtsp://127.0.0.1/live/cam", VideoSdp()));

        RtspResponse response = await flow.HandleAsync(BuildSetup(
            "rtsp://127.0.0.1/live/cam/streamid=0", "RTP/AVP/TCP;unicast;interleaved=0-1;mode=record"));

        response.StatusCode.Should().Be(200);
        response.Headers.Should().ContainKey("Transport");
        response.Headers["Transport"].Should().Contain("interleaved=0-1");

        flow.Channels.Should().ContainKeys(0, 1);
        flow.Channels[0].Kind.Should().Be(SdpMediaKind.Video);
        flow.Channels[0].RtpChannel.Should().Be(0);
        flow.Channels[0].RtcpChannel.Should().Be(1);
        flow.Channels[1].Should().BeSameAs(flow.Channels[0], "RTP and RTCP channels name the same track");
    }

    [Fact]
    public async Task Setup_WithUdpTransport_BindsServerPortsAndEchoesThem()
    {
        RtspServerControlFlow flow = NewFlow();
        await flow.HandleAsync(BuildAnnounce("rtsp://127.0.0.1/live/cam", VideoSdp()));

        RtspResponse response = await flow.HandleAsync(BuildSetup(
            "rtsp://127.0.0.1/live/cam/streamid=0", "RTP/AVP;unicast;client_port=5000-5001"));

        response.StatusCode.Should().Be(200);
        flow.UdpTracks.Should().HaveCount(1, "the UDP track's server sockets were bound");
        string transport = response.Headers["Transport"];
        transport.Should().Contain("client_port=5000-5001", "the client's ports are echoed back");
        transport.Should().Contain("server_port=", "our bound receive ports are advertised");
        flow.Channels.Should().BeEmpty("UDP transport uses no interleaved channel");

        foreach (var track in flow.UdpTracks)
        {
            track.Dispose(); // release the sockets the test bound
        }
    }

    [Fact]
    public async Task Setup_WithNeitherInterleavedNorClientPort_Returns461()
    {
        RtspServerControlFlow flow = NewFlow();
        await flow.HandleAsync(BuildAnnounce("rtsp://127.0.0.1/live/cam", VideoSdp()));

        // a transport with no interleaved= and no client_port= is not something we can serve
        RtspResponse response = await flow.HandleAsync(BuildSetup(
            "rtsp://127.0.0.1/live/cam/streamid=0", "RTP/AVP/BOGUS;unicast"));

        response.StatusCode.Should().Be(461);
        response.ReasonPhrase.Should().Be("Unsupported Transport");
    }

    [Fact]
    public async Task Record_MarksTheSessionRecording()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(
            Request("RECORD rtsp://127.0.0.1/live/cam RTSP/1.0\r\nCSeq: 4\r\n\r\n"));

        response.StatusCode.Should().Be(200);
        flow.Recording.Should().BeTrue();
    }

    [Fact]
    public async Task Teardown_MarksTheSessionTornDown()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(
            Request("TEARDOWN rtsp://127.0.0.1/live/cam RTSP/1.0\r\nCSeq: 5\r\n\r\n"));

        response.StatusCode.Should().Be(200);
        flow.TornDown.Should().BeTrue();
    }

    [Fact]
    public async Task GetParameter_IsAcceptedAsKeepalive()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(
            Request("GET_PARAMETER rtsp://127.0.0.1/live/cam RTSP/1.0\r\nCSeq: 6\r\n\r\n"));

        response.StatusCode.Should().Be(200);
        flow.TornDown.Should().BeFalse("a keepalive must not end the session");
    }

    [Fact]
    public async Task UnknownMethod_Returns405()
    {
        RtspServerControlFlow flow = NewFlow();

        RtspResponse response = await flow.HandleAsync(
            Request("FROBNICATE rtsp://127.0.0.1/live/cam RTSP/1.0\r\nCSeq: 7\r\n\r\n"));

        response.StatusCode.Should().Be(405);
        response.Headers.Should().ContainKey("Allow");
    }

    // =======================================================================
    // helpers

    private static RtspServerControlFlow NewFlow()
    {
        // A dummy pipe pair stands in for the wire; the control flow never reads or writes
        // it (only the connection loop does), so an idle in-memory pipe is enough.
        var inbound = new Pipe();
        var outbound = new Pipe();
        var ctx = new RtspPushReceiverContext(inbound.Reader, outbound.Writer,
            new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var adapter = new RtspMediaFrameAdapter<RtspPushReceiverContext>(ctx);
        return new RtspServerControlFlow(adapter);
    }

    private static RtspMessage Request(string head, string? body = null)
    {
        RtspMessage message = RtspMessage.ParseHead(Encoding.ASCII.GetBytes(head));
        if (body is not null)
        {
            message.Body = Encoding.ASCII.GetBytes(body);
        }
        return message;
    }

    private static RtspMessage BuildAnnounce(string uri, string sdp)
    {
        string head = $"ANNOUNCE {uri} RTSP/1.0\r\nCSeq: 2\r\nContent-Type: application/sdp\r\n" +
            $"Content-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n";
        return Request(head, sdp);
    }

    private static RtspMessage BuildSetup(string uri, string transport) =>
        Request($"SETUP {uri} RTSP/1.0\r\nCSeq: 3\r\nTransport: {transport}\r\n\r\n");

    private static string VideoSdp()
    {
        string sprop = $"{Convert.ToBase64String(s_sps)},{Convert.ToBase64String(s_pps)}";
        return "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Spangle Test\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n" +
            $"m=video 0 RTP/AVP {VideoPayloadType}\r\n" +
            $"a=rtpmap:{VideoPayloadType} H264/90000\r\n" +
            $"a=fmtp:{VideoPayloadType} packetization-mode=1;sprop-parameter-sets={sprop}\r\n" +
            "a=control:streamid=0\r\n";
    }
}
