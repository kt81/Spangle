using System.Buffers.Binary;
using Spangle.Transport.Rtsp.Rtp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// The UDP resequencing buffer: releases RTP in ascending sequence order, tolerates a
/// gap until the window fills, and drops late/duplicate packets. Each test records the
/// sequence numbers released, in order, so behavior is asserted end to end.
/// </summary>
public class RtpReorderBufferTests
{
    /// <summary>A minimal RTP datagram (12-byte header) carrying just the sequence number.</summary>
    private static byte[] Packet(ushort seq)
    {
        var packet = new byte[12];
        packet[0] = 0x80; // version 2
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        return packet;
    }

    private static (RtpReorderBuffer Buffer, List<ushort> Released) NewBuffer(int window = 8)
    {
        var released = new List<ushort>();
        var buffer = new RtpReorderBuffer(window,
            (buf, len) => released.Add(BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2))));
        return (buffer, released);
    }

    [Fact]
    public void InOrderPacketsPassStraightThrough()
    {
        var (buffer, released) = NewBuffer();
        foreach (ushort seq in (ushort[])[10, 11, 12, 13])
        {
            buffer.Add(Packet(seq));
        }
        released.Should().Equal(new ushort[] { 10, 11, 12, 13 });
    }

    [Fact]
    public void ReorderedPacketsAreReleasedInOrder()
    {
        var (buffer, released) = NewBuffer();
        foreach (ushort seq in (ushort[])[10, 12, 11, 13])
        {
            buffer.Add(Packet(seq));
        }
        released.Should().Equal(new ushort[] { 10, 11, 12, 13 }, "12 waits behind 11 until 11 arrives");
    }

    [Fact]
    public void DuplicateAndLatePacketsAreDropped()
    {
        var (buffer, released) = NewBuffer();
        buffer.Add(Packet(10));
        buffer.Add(Packet(11));
        buffer.Add(Packet(10)); // late duplicate of an already-released packet
        buffer.Add(Packet(12));
        released.Should().Equal(new ushort[] { 10, 11, 12 });
    }

    [Fact]
    public void AFullWindowAdvancesPastAMissingPacket()
    {
        var (buffer, released) = NewBuffer(window: 4);
        buffer.Add(Packet(10)); // released; _next = 11
        // 11 never arrives; pile up more than the window behind it
        foreach (ushort seq in (ushort[])[12, 13, 14, 15, 16])
        {
            buffer.Add(Packet(seq));
        }
        released.Should().Equal(new ushort[] { 10, 12, 13, 14, 15, 16 },
            "once the window fills, the buffer gives up on 11 and drains the rest in order");
        released.Should().NotContain((ushort)11);
    }

    [Fact]
    public void FlushReleasesEverythingStillBufferedInOrder()
    {
        var (buffer, released) = NewBuffer();
        buffer.Add(Packet(10)); // _next = 11
        buffer.Add(Packet(13)); // waits for 11, 12
        buffer.Add(Packet(12));
        buffer.Flush();
        released.Should().Equal(new ushort[] { 10, 12, 13 }, "flush drains the tail past the missing 11");
    }

    [Fact]
    public void SequenceNumberWrapIsHandled()
    {
        var (buffer, released) = NewBuffer();
        // straddle the 16-bit wrap: 65534, 65535, 0, 1 arriving slightly out of order
        foreach (ushort seq in (ushort[])[65534, 0, 65535, 1])
        {
            buffer.Add(Packet(seq));
        }
        released.Should().Equal(new ushort[] { 65534, 65535, 0, 1 }, "0 waits behind 65535 across the wrap");
    }
}
