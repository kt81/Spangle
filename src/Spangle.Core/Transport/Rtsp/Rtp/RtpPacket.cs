using System.Buffers.Binary;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>One parsed RTP packet (RFC 3550); Payload excludes header, CSRC, extension and padding.</summary>
internal readonly ref struct RtpPacket
{
    public required byte PayloadType { get; init; }
    public required ushort SequenceNumber { get; init; }
    public required uint Timestamp { get; init; }
    public required uint Ssrc { get; init; }
    public required bool Marker { get; init; }
    public required ReadOnlySpan<byte> Payload { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket packet)
    {
        packet = default;
        if (datagram.Length < 12 || (datagram[0] >> 6) != 2) // version 2
        {
            return false;
        }
        bool hasPadding = (datagram[0] & 0x20) != 0;
        bool hasExtension = (datagram[0] & 0x10) != 0;
        int csrcCount = datagram[0] & 0x0F;

        int offset = 12 + csrcCount * 4;
        if (datagram.Length < offset)
        {
            return false;
        }
        if (hasExtension)
        {
            if (datagram.Length < offset + 4)
            {
                return false;
            }
            int extensionWords = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            offset += 4 + extensionWords * 4;
            if (datagram.Length < offset)
            {
                return false;
            }
        }

        int end = datagram.Length;
        if (hasPadding)
        {
            int padding = datagram[^1];
            if (padding == 0 || end - offset < padding)
            {
                return false;
            }
            end -= padding;
        }

        packet = new RtpPacket
        {
            PayloadType = (byte)(datagram[1] & 0x7F),
            Marker = (datagram[1] & 0x80) != 0,
            SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]),
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]),
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]),
            Payload = datagram[offset..end],
        };
        return true;
    }
}
