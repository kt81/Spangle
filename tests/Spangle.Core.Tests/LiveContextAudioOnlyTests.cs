using System.IO.Pipelines;
using System.Net;
using Spangle.Transport.HLS;

namespace Spangle.Tests;

/// <summary>
/// RTMP cannot declare "no video is coming", so the session policy may: with the
/// fallback enabled, audio-with-no-video wires the pipeline as audio-only after the
/// grace period; a video codec arriving in time keeps the normal wiring.
/// </summary>
public class LiveContextAudioOnlyTests
{
    [Fact]
    public async Task AudioOnlyFallbackWiresWithoutVideo()
    {
        var dummy = new Pipe();
        var receiver = new FakeReceiverContext(dummy.Reader, dummy.Writer);
        var hls = new HLSSenderContext(CancellationToken.None);
        using var live = new LiveContext(receiver, hls,
            audioOnlyFallback: TimeSpan.FromMilliseconds(100));

        receiver.AudioCodec = AudioCodec.AAC;

        await WaitUntilAsync(() => receiver.MediaOutlet is not null);
        receiver.IsAudioOnly.Should().BeTrue("no video appeared within the fallback window");
    }

    [Fact]
    public async Task VideoInTimeKeepsTheNormalWiring()
    {
        var dummy = new Pipe();
        var receiver = new FakeReceiverContext(dummy.Reader, dummy.Writer);
        var hls = new HLSSenderContext(CancellationToken.None);
        using var live = new LiveContext(receiver, hls,
            audioOnlyFallback: TimeSpan.FromMilliseconds(200));

        receiver.AudioCodec = AudioCodec.AAC;
        receiver.VideoCodec = VideoCodec.H264; // wires immediately

        receiver.MediaOutlet.Should().NotBeNull();
        await Task.Delay(600);
        receiver.IsAudioOnly.Should().BeFalse("the fallback must not fire after normal wiring");
    }

    [Fact]
    public async Task NeverWiredSessionCompletesTheSenderIntake()
    {
        var dummy = new Pipe();
        var receiver = new FakeReceiverContext(dummy.Reader, dummy.Writer);
        var hls = new HLSSenderContext(CancellationToken.None);
        using var live = new LiveContext(receiver, hls);

        await live.StartAsync(); // the fake receiver returns immediately; nothing wired

        // the sender must not be left waiting forever on its intake
        ReadResult result = await hls.IntakeReader.ReadAsync(new CancellationTokenSource(3000).Token);
        result.IsCompleted.Should().BeTrue();
    }

    // =======================================================================

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        condition().Should().BeTrue("the condition must hold within the wait budget");
    }

    private sealed class FakeReceiverContext(PipeReader reader, PipeWriter writer)
        : ReceiverContextBase<FakeReceiverContext>(reader, writer, CancellationToken.None)
    {
        public override string Id => "test";
        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override bool IsCompleted => false;
        public override ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource) =>
            ValueTask.CompletedTask;
    }
}
