using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="AacDepacketizer"/> for AAC-hbr (RFC 3640, sizeLength/indexLength/
/// indexDeltaLength = 13/3/3). Each RTP packet begins with a 16-bit AU-headers
/// length in bits, then one AU-header per access unit (a 13-bit size and a
/// 3-bit index/index-delta), then the concatenated AU payloads.
/// </summary>
public class AacDepacketizerTests
{
    private const int SizeLength = 13;
    private const int IndexLength = 3;
    private const int IndexDeltaLength = 3;

    [Fact]
    public void SingleAuIsReportedWithIndexZero()
    {
        List<Au> results = Collect(out AacDepacketizer d);
        byte[] au = [0x21, 0x22, 0x23, 0x24];

        // AU-headers section is one 16-bit header (13-bit size + 3-bit index) = 16 bits.
        byte[] payload = BuildPacket(auHeaderBits: 16, [Header(au.Length, index: 0)], au);
        Feed(d, timestamp: 12345, payload);

        results.Should().ContainSingle();
        results[0].Index.Should().Be(0);
        results[0].Timestamp.Should().Be(12345u);
        results[0].Bytes.Should().Equal(au);
    }

    [Fact]
    public void TwoAusAreReportedInOrderWithIncrementingIndices()
    {
        List<Au> results = Collect(out AacDepacketizer d);
        byte[] au0 = [0x31, 0x32, 0x33, 0x34];
        byte[] au1 = [0x41, 0x42, 0x43];

        // Two 16-bit AU-headers = 32 bits; the second header's index field is the 3-bit index-delta.
        byte[] payload = BuildPacket(auHeaderBits: 32,
            [Header(au0.Length, index: 0), Header(au1.Length, index: 0)], au0, au1);
        Feed(d, timestamp: 500, payload);

        results.Should().HaveCount(2);
        results[0].Index.Should().Be(0);
        results[0].Bytes.Should().Equal(au0);
        results[1].Index.Should().Be(1);
        results[1].Bytes.Should().Equal(au1);
        results.Should().OnlyContain(r => r.Timestamp == 500u);
    }

    // =======================================================================

    private sealed record Au(byte[] Bytes, uint Timestamp, int Index);

    private static List<Au> Collect(out AacDepacketizer depacketizer)
    {
        var results = new List<Au>();
        depacketizer = new AacDepacketizer(SizeLength, IndexLength, IndexDeltaLength,
            (au, ts, index) => results.Add(new Au(au, ts, index)));
        return results;
    }

    private static void Feed(AacDepacketizer d, uint timestamp, byte[] payload)
    {
        var packet = new RtpPacket
        {
            PayloadType = 97,
            SequenceNumber = 0,
            Timestamp = timestamp,
            Ssrc = 0,
            Marker = true,
            Payload = payload,
        };
        d.Feed(packet);
    }

    /// <summary>One 16-bit AU-header: the 13-bit AU size in the top bits, the 3-bit index in the low bits.</summary>
    private static ushort Header(int auSize, int index) => (ushort)(((auSize & 0x1FFF) << IndexLength) | (index & 0x7));

    private static byte[] BuildPacket(int auHeaderBits, ushort[] headers, params byte[][] aus)
    {
        var dataLength = 0;
        foreach (byte[] au in aus)
        {
            dataLength += au.Length;
        }
        byte[] payload = new byte[2 + (headers.Length * 2) + dataLength];
        // 16-bit AU-headers-length, expressed in bits (RFC 3640 §3.2.1).
        payload[0] = (byte)(auHeaderBits >> 8);
        payload[1] = (byte)auHeaderBits;
        var offset = 2;
        foreach (ushort header in headers)
        {
            payload[offset] = (byte)(header >> 8);
            payload[offset + 1] = (byte)header;
            offset += 2;
        }
        foreach (byte[] au in aus)
        {
            au.CopyTo(payload.AsSpan(offset));
            offset += au.Length;
        }
        return payload;
    }
}
