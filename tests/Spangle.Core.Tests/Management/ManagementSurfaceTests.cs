using Microsoft.Extensions.Logging;
using Spangle.Extensions.Kestrel.Management;

namespace Spangle.Tests.Management;

public class SpangleLogBufferTests
{
    [Fact]
    public void SnapshotFiltersByLevelAndKeepsTheNewest()
    {
        var buffer = new SpangleLogBuffer();
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Debug, "cat", $"debug {i}", null));
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Warning, "cat", $"warn {i}", null));
        }

        IReadOnlyList<LogEntry> warnings = buffer.Snapshot(max: 100, minLevel: LogLevel.Warning);
        warnings.Should().HaveCount(10).And.OnlyContain(static e => e.Level >= LogLevel.Warning);

        IReadOnlyList<LogEntry> newestThree = buffer.Snapshot(max: 3, minLevel: LogLevel.Trace);
        newestThree.Should().HaveCount(3);
        newestThree[^1].Message.Should().Be("warn 9", "the snapshot keeps the newest entries");
    }

    [Fact]
    public void TheRingDropsTheOldestEntries()
    {
        var buffer = new SpangleLogBuffer();
        for (var i = 0; i < 3000; i++)
        {
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Information, "cat", $"m{i}", null));
        }

        IReadOnlyList<LogEntry> all = buffer.Snapshot(max: 4096);
        all.Should().HaveCount(2048, "the ring capacity bounds memory");
        all[0].Message.Should().Be("m952", "the oldest entries are gone");
        all[^1].Message.Should().Be("m2999");
    }

    [Fact]
    public async Task SubscribersReceiveNewEntries()
    {
        var buffer = new SpangleLogBuffer();
        using var cts = new CancellationTokenSource(5000);

        var received = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pump = Task.Run(async () =>
        {
            await foreach (LogEntry entry in buffer.SubscribeAsync(cts.Token))
            {
                received.TrySetResult(entry);
                break;
            }
        }, cts.Token);

        // the subscriber registers asynchronously; retry until the entry lands
        while (!received.Task.IsCompleted && !cts.IsCancellationRequested)
        {
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Error, "cat", "live!", null));
            await Task.Delay(10, cts.Token);
        }

        (await received.Task).Message.Should().Be("live!");
        await pump;
    }
}

public class ViewerStatsRegistryTests
{
    [Fact]
    public void CountsRequestsAndWaitersPerStream()
    {
        var viewers = new ViewerStatsRegistry();

        viewers.OnPlaylistRequest("a");
        viewers.OnPlaylistRequest("a");
        viewers.OnPlaylistRequest("b");
        viewers.WaiterEntered("a");
        viewers.WaiterEntered("a");
        viewers.WaiterExited("a");

        viewers.Get("a").Should().Be((2L, 1));
        viewers.Get("b").Should().Be((1L, 0));
        viewers.Get("missing").Should().Be((0L, 0));
    }
}
