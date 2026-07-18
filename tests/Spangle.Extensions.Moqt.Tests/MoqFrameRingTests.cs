using System.Buffers;
using System.Text;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The bounded buffer between the live pipeline and the relay: enqueue never blocks, overflow
/// drops the <em>oldest</em> frames, and a video drop is reported so the consumer can resume at
/// a keyframe. This is what keeps a slow subscribed relay from pushing back through the fan-out
/// and stalling the primary HLS output.
/// </summary>
public class MoqFrameRingTests
{
    private static MediaFrameHeader Header(MediaFrameKind kind, int length, uint timestamp) => new()
    {
        Kind = kind,
        Codec = 1,
        Length = length,
        Timestamp = timestamp,
    };

    private static void Enqueue(MoqFrameRing ring, MediaFrameKind kind, string payload, uint timestamp)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        ring.Enqueue(Header(kind, bytes.Length, timestamp), new ReadOnlySequence<byte>(bytes));
    }

    [Fact]
    public async Task FramesComeOutInOrder_AndCompleteEndsTheDrain()
    {
        using var ring = new MoqFrameRing(capacityBytes: 1024);
        Enqueue(ring, MediaFrameKind.Video, "one", timestamp: 1);
        Enqueue(ring, MediaFrameKind.Audio, "two", timestamp: 2);
        ring.Complete();

        (RingFrame? first, int dropped, bool videoDropped) = await ring.DequeueAsync(CancellationToken.None);
        dropped.Should().Be(0);
        videoDropped.Should().BeFalse();
        Encoding.UTF8.GetString(first!.Value.Payload.Span).Should().Be("one");
        first.Value.Return();

        (RingFrame? second, _, _) = await ring.DequeueAsync(CancellationToken.None);
        Encoding.UTF8.GetString(second!.Value.Payload.Span).Should().Be("two");
        second.Value.Return();

        (RingFrame? end, _, _) = await ring.DequeueAsync(CancellationToken.None);
        end.Should().BeNull("Complete means no more frames are coming");
    }

    [Fact]
    public async Task Overflow_DropsTheOldest_AndReportsTheVideoDrop()
    {
        // Each payload is 8 bytes; a 20-byte budget holds two. The third enqueue must evict
        // the oldest — and never block, because blocking here is the HLS-stalling failure
        // this type exists to prevent.
        using var ring = new MoqFrameRing(capacityBytes: 20);
        Enqueue(ring, MediaFrameKind.Video, "video--1", timestamp: 1);
        Enqueue(ring, MediaFrameKind.Audio, "audio--2", timestamp: 2);
        Enqueue(ring, MediaFrameKind.Audio, "audio--3", timestamp: 3);

        (RingFrame? first, int dropped, bool videoDropped) = await ring.DequeueAsync(CancellationToken.None);
        dropped.Should().Be(1, "the oldest frame fell off to stay in budget");
        videoDropped.Should().BeTrue("the dropped frame was video, so the group must be abandoned");
        Encoding.UTF8.GetString(first!.Value.Payload.Span).Should().Be("audio--2",
            "the drop takes the oldest, not the newest");
        first.Value.Return();

        (RingFrame? second, int droppedAfter, bool videoAfter) = await ring.DequeueAsync(CancellationToken.None);
        droppedAfter.Should().Be(0, "the drop report is consumed by the first dequeue after it");
        videoAfter.Should().BeFalse();
        Encoding.UTF8.GetString(second!.Value.Payload.Span).Should().Be("audio--3");
        second.Value.Return();
    }

    [Fact]
    public async Task AFrameLargerThanTheWholeBudget_StillGoesThroughAlone()
    {
        using var ring = new MoqFrameRing(capacityBytes: 4);
        Enqueue(ring, MediaFrameKind.Video, "bigger-than-budget", timestamp: 1);
        ring.Complete();

        (RingFrame? frame, _, _) = await ring.DequeueAsync(CancellationToken.None);
        Encoding.UTF8.GetString(frame!.Value.Payload.Span).Should().Be("bigger-than-budget",
            "the newest frame always survives; a giant frame is delivered, not deadlocked");
        frame.Value.Return();
    }

    [Fact]
    public async Task Cancellation_UnblocksAWaitingDequeue()
    {
        using var ring = new MoqFrameRing(capacityBytes: 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Func<Task> act = async () => await ring.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
