using System.Buffers.Binary;
using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// <see cref="RtcpSenderReport.TryFindSenderReport"/> over hand-built RTCP
/// (RFC 3550 §6.4): it reads the SSRC / NTP / RTP fields of a Sender Report
/// (packet type 200) and finds it even when it trails other packets in a
/// compound RTCP datagram.
/// </summary>
public class RtcpSenderReportTests
{
    private const byte Version2Rc0 = 0x80; // V=2, P=0, RC=0
    private const byte PtSenderReport = 200;
    private const byte PtReceiverReport = 201;

    [Fact]
    public void ParsesTheSenderReportFields()
    {
        const uint ssrc = 0x0A0B0C0D;
        ulong ntp = 0xE1E2E3E4_F1F2F3F4; // 32-bit seconds . 32-bit fraction
        const uint rtpTimestamp = 0x00BC614E;

        byte[] sr = BuildSenderReport(ssrc, ntp, rtpTimestamp);

        RtcpSenderReport.TryFindSenderReport(sr, out RtcpSenderReport report).Should().BeTrue();
        report.Ssrc.Should().Be(ssrc);
        report.NtpTimestamp.Should().Be(ntp);
        report.RtpTimestamp.Should().Be(rtpTimestamp);

        // NtpMilliseconds = seconds*1000 + fraction/2^32*1000
        double expectedMs = ((ntp >> 32) * 1000.0) + ((uint)ntp * 1000.0 / 4294967296.0);
        report.NtpMilliseconds.Should().BeApproximately(expectedMs, 1e-6);
    }

    [Fact]
    public void FindsTheSenderReportAfterAReceiverReportInACompoundPacket()
    {
        const uint ssrc = 0x11112222;
        ulong ntp = 0x0000000A_80000000; // 10 seconds + half a second
        const uint rtpTimestamp = 0x12345678;

        byte[] rr = BuildEmptyReceiverReport(0x99998888);
        byte[] sr = BuildSenderReport(ssrc, ntp, rtpTimestamp);
        byte[] compound = [.. rr, .. sr];

        RtcpSenderReport.TryFindSenderReport(compound, out RtcpSenderReport report).Should().BeTrue();
        report.Ssrc.Should().Be(ssrc);
        report.RtpTimestamp.Should().Be(rtpTimestamp);
        report.NtpMilliseconds.Should().BeApproximately(10500.0, 1e-6);
    }

    [Fact]
    public void ReturnsFalseWhenThereIsNoSenderReport()
    {
        byte[] rr = BuildEmptyReceiverReport(0x0);
        RtcpSenderReport.TryFindSenderReport(rr, out _).Should().BeFalse();
    }

    // =======================================================================

    private static byte[] BuildSenderReport(uint ssrc, ulong ntp, uint rtpTimestamp)
    {
        // header(4) + sender info: SSRC(4) + NTP(8) + RTP ts(4) + packet count(4) + octet count(4) = 28 bytes.
        byte[] packet = new byte[28];
        packet[0] = Version2Rc0;
        packet[1] = PtSenderReport;
        // length is the packet size in 32-bit words minus one (28 bytes = 7 words -> 6).
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTimestamp);
        // bytes 20..27 (sender's packet/octet counts) are left zero; the parser ignores them.
        return packet;
    }

    private static byte[] BuildEmptyReceiverReport(uint reporterSsrc)
    {
        // header(4) + reporter SSRC(4), no report blocks = 8 bytes = 2 words -> length 1.
        byte[] packet = new byte[8];
        packet[0] = Version2Rc0;
        packet[1] = PtReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), reporterSsrc);
        return packet;
    }
}
