using System.Text;
using Spangle.Transport.Rtsp.Sdp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="SdpSession.Parse(string)"/> extracts exactly the media it acts on
/// (RFC 8866 SDP / RFC 6184 &amp; RFC 3640 payload conventions): per-track payload
/// type, encoding, clock rate, fmtp parameters, and the aggregate/per-track control
/// URLs used to address SETUP.
/// </summary>
public class SdpParserTests
{
    // A realistic H.264 + AAC DESCRIBE answer: session-level a=control, an H.264
    // video track (rtpmap/fmtp with packetization-mode and sprop-parameter-sets)
    // and an MPEG4-GENERIC (AAC-hbr) audio track with its AudioSpecificConfig.
    private const string SpropParameterSets = "Z0IADYABkPZAAAADAEAAAAwPI8YMkA==,aM4G4g==";
    private const string H264AacSdp =
        "v=0\r\n" +
        "o=- 1234567890 1 IN IP4 192.168.1.10\r\n" +
        "s=Media Presentation\r\n" +
        "c=IN IP4 0.0.0.0\r\n" +
        "t=0 0\r\n" +
        "a=control:rtsp://192.168.1.10/stream\r\n" +
        "m=video 0 RTP/AVP 96\r\n" +
        "a=rtpmap:96 H264/90000\r\n" +
        "a=fmtp:96 packetization-mode=1;profile-level-id=42001f;sprop-parameter-sets=" + SpropParameterSets + "\r\n" +
        "a=control:trackID=1\r\n" +
        "m=audio 0 RTP/AVP 97\r\n" +
        "a=rtpmap:97 MPEG4-GENERIC/44100/2\r\n" +
        "a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210\r\n" +
        "a=control:trackID=2\r\n";

    [Fact]
    public void ParsesBothTracksWithTheirMediaAttributes()
    {
        SdpSession sdp = SdpSession.Parse(H264AacSdp);

        sdp.SessionControl.Should().Be("rtsp://192.168.1.10/stream");
        sdp.Media.Should().HaveCount(2);
    }

    [Fact]
    public void ParsesTheH264VideoTrack()
    {
        SdpSession sdp = SdpSession.Parse(H264AacSdp);
        SdpMedia video = sdp.Media[0];

        video.Kind.Should().Be(SdpMediaKind.Video);
        video.PayloadType.Should().Be(96);
        video.Encoding.Should().Be("H264"); // rtpmap encoding is upper-cased
        video.ClockRate.Should().Be(90000u);
        video.Channels.Should().Be(1, "video defaults to a single channel");
        video.Control.Should().Be("trackID=1");

        video.FmtpValue("packetization-mode").Should().Be("1");
        video.FmtpValue("sprop-parameter-sets").Should().Be(SpropParameterSets);
        video.FmtpValue("profile-level-id").Should().Be("42001f");
    }

    [Fact]
    public void ParsesTheAacAudioTrack()
    {
        SdpSession sdp = SdpSession.Parse(H264AacSdp);
        SdpMedia audio = sdp.Media[1];

        audio.Kind.Should().Be(SdpMediaKind.Audio);
        audio.PayloadType.Should().Be(97);
        audio.Encoding.Should().Be("MPEG4-GENERIC");
        audio.ClockRate.Should().Be(44100u);
        audio.Channels.Should().Be(2, "rtpmap carries the channel count as the third field");
        audio.Control.Should().Be("trackID=2");

        audio.FmtpValue("mode").Should().Be("AAC-hbr");
        audio.FmtpValue("config").Should().Be("1210");
        // fmtp keys are matched case-insensitively
        audio.FmtpValue("sizeLength").Should().Be("13");
        audio.FmtpValue("indexDeltaLength").Should().Be("3");
    }

    [Fact]
    public void MissingFmtpKeyReturnsNull()
    {
        SdpSession sdp = SdpSession.Parse(H264AacSdp);
        sdp.Media[0].FmtpValue("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void ParsesFromUtf8BytesIdenticallyToString()
    {
        SdpSession fromBytes = SdpSession.Parse(Encoding.UTF8.GetBytes(H264AacSdp).AsSpan());

        fromBytes.Media.Should().HaveCount(2);
        fromBytes.Media[0].Encoding.Should().Be("H264");
        fromBytes.Media[1].Encoding.Should().Be("MPEG4-GENERIC");
    }

    [Fact]
    public void ParsesAVideoOnlyDescription()
    {
        const string videoOnly =
            "v=0\r\n" +
            "o=- 1 1 IN IP4 10.0.0.1\r\n" +
            "s=Cam\r\n" +
            "t=0 0\r\n" +
            "m=video 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1\r\n" +
            "a=control:trackID=0\r\n";

        SdpSession sdp = SdpSession.Parse(videoOnly);

        sdp.SessionControl.Should().BeNull("this description has no session-level a=control");
        sdp.Media.Should().ContainSingle();
        sdp.Media[0].Kind.Should().Be(SdpMediaKind.Video);
        sdp.Media[0].Encoding.Should().Be("H264");
        sdp.Media[0].Control.Should().Be("trackID=0");
        sdp.Media[0].FmtpValue("packetization-mode").Should().Be("1");
    }
}
