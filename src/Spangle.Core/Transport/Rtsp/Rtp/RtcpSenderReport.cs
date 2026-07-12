using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// The one RTCP packet this receiver reads: a Sender Report (PT=200) anchors a
/// track's RTP timestamps to the sender's NTP wallclock, which is what aligns
/// audio and video onto one timeline.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct RtcpSenderReport
{
    public required uint Ssrc { get; init; }
    public required ulong NtpTimestamp { get; init; }
    public required uint RtpTimestamp { get; init; }

    /// <summary>The NTP timestamp as milliseconds (seconds×1000 + fraction).</summary>
    public double NtpMilliseconds => (NtpTimestamp >> 32) * 1000.0 + (uint)NtpTimestamp * 1000.0 / 4294967296.0;

    /// <summary>Scans a compound RTCP packet for the first Sender Report.</summary>
    public static bool TryFindSenderReport(ReadOnlySpan<byte> compound, out RtcpSenderReport report)
    {
        report = default;
        while (compound.Length >= 8)
        {
            if ((compound[0] >> 6) != 2) // version 2
            {
                return false;
            }
            int packetType = compound[1];
            int length = (BinaryPrimitives.ReadUInt16BigEndian(compound[2..]) + 1) * 4;
            if (length > compound.Length)
            {
                return false;
            }
            if (packetType == 200 && length >= 28)
            {
                report = new RtcpSenderReport
                {
                    Ssrc = BinaryPrimitives.ReadUInt32BigEndian(compound[4..]),
                    NtpTimestamp = BinaryPrimitives.ReadUInt64BigEndian(compound[8..]),
                    RtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(compound[16..]),
                };
                return true;
            }
            compound = compound[length..];
        }
        return false;
    }
}
