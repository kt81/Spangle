using System.Buffers;
using Spangle.Containers.M2TS;
using Spangle.Transport.SRT;

namespace Spangle.Tests.Transport.SRT;

/// <summary>
/// The 0x47-resync loop: after alignment loss, the receiver must latch onto a sync
/// byte only when another sync byte sits exactly one packet later, so a 0x47 inside
/// a payload cannot fool it.
/// </summary>
public class SrtResyncTests
{
    private const int PacketSize = 188; // M2TSWriter.PacketSize

    private static byte[] Packet(byte fill)
    {
        var packet = new byte[PacketSize];
        Array.Fill(packet, fill);
        packet[0] = 0x47;
        return packet;
    }

    [Fact]
    public void ResyncSkipsGarbageToTheVerifiedBoundary()
    {
        byte[] garbage = [0xFF, 0x00, 0x12, 0x34, 0xFF];
        var buff = new ReadOnlySequence<byte>([.. garbage, .. Packet(0x00), .. Packet(0x01)]);

        SRTReceiverContext.TryResync(ref buff, out bool needMoreData).Should().BeTrue();

        needMoreData.Should().BeFalse();
        buff.Length.Should().Be(2 * PacketSize);
        buff.FirstSpan[0].Should().Be(0x47);
    }

    [Fact]
    public void PayloadSyncByteDoesNotFoolTheResync()
    {
        // a fake 0x47 at index 1: one packet after it lands inside packet bytes
        // that are all zero, so the candidate fails verification
        byte[] prefix = [0xFF, 0x47, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var buff = new ReadOnlySequence<byte>([.. prefix, .. Packet(0x00), .. Packet(0x00)]);

        SRTReceiverContext.TryResync(ref buff, out bool needMoreData).Should().BeTrue();

        needMoreData.Should().BeFalse();
        buff.Length.Should().Be(2 * PacketSize, "the fake sync in the garbage must be skipped");
        buff.FirstSpan[0].Should().Be(0x47);
    }

    [Fact]
    public void UnverifiableCandidateWaitsForMoreData()
    {
        // a sync byte exists, but fewer than PacketSize+1 bytes follow it
        byte[] tail = new byte[20];
        tail[0] = 0xFF;
        tail[5] = 0x47;
        var buff = new ReadOnlySequence<byte>(tail);

        SRTReceiverContext.TryResync(ref buff, out bool needMoreData).Should().BeFalse();

        needMoreData.Should().BeTrue("the candidate needs the next read to verify");
        buff.FirstSpan[0].Should().Be(0x47, "the tail from the candidate on must be kept");
    }

    [Fact]
    public void PureGarbageIsDroppedEntirely()
    {
        byte[] garbage = new byte[300]; // no 0x47 anywhere
        Array.Fill(garbage, (byte)0xAA);
        var buff = new ReadOnlySequence<byte>(garbage);

        SRTReceiverContext.TryResync(ref buff, out bool needMoreData).Should().BeFalse();
        needMoreData.Should().BeFalse("nothing in the buffer is worth keeping");
    }

    [Fact]
    public void PacketSizeConstantMatchesTheMuxer()
    {
        M2TSWriter.PacketSize.Should().Be(PacketSize);
    }
}
