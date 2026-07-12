using System.Buffers.Binary;
using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="RtpPacket.TryParse"/> against hand-built RTP datagrams (RFC 3550 §5.1):
/// the fixed 12-byte header, the optional CSRC list, the header extension, and
/// trailing padding all get their bytes stripped so <see cref="RtpPacket.Payload"/>
/// is exactly the media payload.
/// </summary>
public class RtpPacketTests
{
    private const byte Version2 = 0x80; // V=2, no padding/extension, CC=0
    private const byte PayloadTypeH264 = 96;

    [Fact]
    public void ParsesAMinimalTwelveByteHeader()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] datagram = new byte[12 + payload.Length];
        datagram[0] = Version2;
        datagram[1] = 0x80 | PayloadTypeH264; // marker bit set + PT 96
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), 0x1234);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), 0xAABBCCDDu);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(8), 0x11223344u);
        payload.CopyTo(datagram.AsSpan(12));

        RtpPacket.TryParse(datagram, out RtpPacket packet).Should().BeTrue();

        packet.PayloadType.Should().Be(PayloadTypeH264);
        packet.Marker.Should().BeTrue();
        packet.SequenceNumber.Should().Be(0x1234);
        packet.Timestamp.Should().Be(0xAABBCCDDu);
        packet.Ssrc.Should().Be(0x11223344u);
        packet.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void SkipsTheCsrcListSoPayloadStartsAfterIt()
    {
        byte[] payload = [1, 2, 3];
        const int csrcCount = 2;
        byte[] datagram = new byte[12 + (csrcCount * 4) + payload.Length];
        datagram[0] = 0x80 | csrcCount; // V=2, CC=2
        datagram[1] = PayloadTypeH264; // marker clear
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), 7);
        // two CSRC identifiers occupy bytes 12..19 and must be excluded from the payload
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(12), 0xCAFEBABEu);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16), 0xF00DF00Du);
        payload.CopyTo(datagram.AsSpan(20));

        RtpPacket.TryParse(datagram, out RtpPacket packet).Should().BeTrue();

        packet.Marker.Should().BeFalse();
        packet.SequenceNumber.Should().Be(7);
        packet.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void SkipsTheHeaderExtension()
    {
        byte[] payload = [0x55, 0x66];
        const int extensionWords = 2; // 32-bit words of extension data after its 4-byte header
        byte[] datagram = new byte[12 + 4 + (extensionWords * 4) + payload.Length];
        datagram[0] = 0x80 | 0x10; // V=2, X=1
        datagram[1] = PayloadTypeH264;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), 42);
        // extension header: 16-bit profile id, then the 16-bit length in words
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(12), 0xBEDE);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(14), extensionWords);
        payload.CopyTo(datagram.AsSpan(12 + 4 + (extensionWords * 4)));

        RtpPacket.TryParse(datagram, out RtpPacket packet).Should().BeTrue();

        packet.SequenceNumber.Should().Be(42);
        packet.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void TrimsTrailingPadding()
    {
        byte[] payload = [0xA0, 0xA1, 0xA2];
        const byte paddingLength = 4; // the last byte counts itself; 4 trailing bytes are padding
        byte[] datagram = new byte[12 + payload.Length + paddingLength];
        datagram[0] = 0x80 | 0x20; // V=2, P=1
        datagram[1] = PayloadTypeH264;
        payload.CopyTo(datagram.AsSpan(12));
        datagram[^1] = paddingLength;

        RtpPacket.TryParse(datagram, out RtpPacket packet).Should().BeTrue();

        packet.Payload.ToArray().Should().Equal(payload); // padding excluded
    }

    [Fact]
    public void RejectsATooShortBuffer()
    {
        byte[] datagram = new byte[11]; // one byte short of the 12-byte header
        datagram[0] = Version2;

        RtpPacket.TryParse(datagram, out _).Should().BeFalse();
    }

    [Fact]
    public void RejectsTheWrongVersion()
    {
        byte[] datagram = new byte[12];
        datagram[0] = 0x40; // V=1

        RtpPacket.TryParse(datagram, out _).Should().BeFalse();
    }
}
