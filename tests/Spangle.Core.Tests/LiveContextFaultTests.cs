using System.IO.Pipelines;
using System.Net;
using Spangle.Spinner;
using Spangle.Transport.HLS;

namespace Spangle.Tests;

/// <summary>
/// Silent-stop eradication: a pipeline stage that throws must end the session rather than leave the
/// receiver blocked forever on a stream nothing is draining.
/// </summary>
public class LiveContextFaultTests
{
    [Fact]
    public async Task AFaultedSpinnerEndsTheSession()
    {
        var dummy = new Pipe();
        var receiver = new BlockingReceiverContext(dummy.Reader, dummy.Writer);
        var hls = new HLSSenderContext(CancellationToken.None);
        var faulty = new ThrowingSpinner();
        using var live = new LiveContext(receiver, hls, mediaSpinners: [faulty]);

        Task session = live.StartAsync().AsTask();
        // Wiring the pipeline begins the spinner, which throws; that must shut the session down.
        receiver.VideoCodec = VideoCodec.H264;

        Func<Task> waitForEnd = () => session.WaitAsync(TimeSpan.FromSeconds(5));
        await waitForEnd.Should().NotThrowAsync("the faulted spinner must end the blocked session");
        session.IsCompletedSuccessfully.Should().BeTrue();
    }

    private sealed class ThrowingSpinner : SpinnerBase<ThrowingSpinner>
    {
        public ThrowingSpinner() : base(CancellationToken.None)
        {
        }

        public override async ValueTask SpinAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class BlockingReceiverContext(PipeReader reader, PipeWriter writer)
        : ReceiverContextBase<BlockingReceiverContext>(reader, writer, CancellationToken.None)
    {
        public override string Id => "test";
        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override bool IsCompleted => false;

        public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
        {
            // Block as a real receiver would while waiting for peer data; only cancellation —
            // host shutdown, or Shutdown() from the fault handler — ends the wait.
            try
            {
                await Task.Delay(Timeout.Infinite, CancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
