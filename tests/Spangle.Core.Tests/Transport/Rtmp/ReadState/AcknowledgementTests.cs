using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using Spangle.Transport.Rtmp;
using Spangle.Transport.Rtmp.ProtocolControlMessage;

namespace Spangle.Tests.Transport.Rtmp.ReadState;

/// <summary>
/// Acknowledgement (RTMP 5.4.3): once the peer advertises a Window Acknowledgement Size, the
/// receiver must acknowledge every window's worth of received bytes with the running byte count.
/// </summary>
public class AcknowledgementTests
{
    [Fact]
    public async Task AcknowledgesOnceAWindowOfBytesHasArrived()
    {
        var tc = new TestContext();
        RtmpReceiverContext ctx = tc.Context;
        ctx.SetPeerAckWindowSize(1000);

        // Below the window: nothing goes out yet.
        ctx.AddBytesReceived(999);
        await ctx.MaybeAcknowledgeAsync();
        (await ReadSentAsync(tc)).Should().BeEmpty("no acknowledgement before a full window arrives");

        // Crossing the window sends one Acknowledgement carrying the running byte count.
        ctx.AddBytesReceived(1);
        await ctx.MaybeAcknowledgeAsync();

        byte[] sent = await ReadSentAsync(tc);
        (MessageType type, uint sequenceNumber) = ParseControlMessage(sent);
        type.Should().Be(MessageType.Acknowledgement);
        sequenceNumber.Should().Be(1000u, "the sequence number is the total bytes received");
    }

    [Fact]
    public async Task DoesNotAcknowledgeUntilThePeerAdvertisesAWindow()
    {
        var tc = new TestContext();
        RtmpReceiverContext ctx = tc.Context;

        // No window advertised: even a large amount of data owes no acknowledgement.
        ctx.AddBytesReceived(1_000_000);
        await ctx.MaybeAcknowledgeAsync();
        (await ReadSentAsync(tc)).Should().BeEmpty("no window advertised, so no acknowledgement is owed");
    }

    // A control message the writer emits as Fmt0 on the control chunk stream:
    // [basic header:1][timestamp:3][length:3][type:1][stream id:4][payload].
    private static (MessageType Type, uint SequenceNumber) ParseControlMessage(byte[] chunk)
    {
        var type = (MessageType)chunk[7];
        uint sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(12, 4));
        return (type, sequenceNumber);
    }

    private static async Task<byte[]> ReadSentAsync(TestContext tc)
    {
        if (!tc.SendPipe.Reader.TryRead(out ReadResult result))
        {
            return [];
        }
        byte[] bytes = result.Buffer.ToArray();
        tc.SendPipe.Reader.AdvanceTo(result.Buffer.End);
        return bytes;
    }
}
