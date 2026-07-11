using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Spangle.Extensions.Kestrel.Management;

/// <summary>One captured log record, as served by the management log endpoints.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>
/// In-memory ring of recent log records plus a live broadcast for SSE tails.
/// Slow subscribers lose their oldest queued entries, never block the logger.
/// </summary>
public sealed class SpangleLogBuffer
{
    private const int Capacity = 2048;
    private const int SubscriberQueueLength = 512;

    private readonly Lock _lock = new();
    private readonly LogEntry?[] _ring = new LogEntry?[Capacity];
    private int _head; // next write position
    private int _count;
    private readonly List<ChannelWriter<LogEntry>> _subscribers = [];

    public void Add(LogEntry entry)
    {
        ChannelWriter<LogEntry>[]? subscribers = null;
        lock (_lock)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
            if (_subscribers.Count > 0)
            {
                subscribers = [.. _subscribers];
            }
        }
        if (subscribers is not null)
        {
            foreach (ChannelWriter<LogEntry> writer in subscribers)
            {
                writer.TryWrite(entry); // bounded drop-oldest; never blocks
            }
        }
    }

    /// <summary>The most recent <paramref name="max"/> records at or above <paramref name="minLevel"/>, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot(int max = 500, LogLevel minLevel = LogLevel.Trace)
    {
        var result = new List<LogEntry>(Math.Min(max, Capacity));
        lock (_lock)
        {
            int start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                LogEntry? entry = _ring[(start + i) % Capacity];
                if (entry is not null && entry.Level >= minLevel)
                {
                    result.Add(entry);
                }
            }
        }
        return result.Count > max ? result[^max..] : result;
    }

    /// <summary>Streams records as they arrive, until cancellation.</summary>
    public async IAsyncEnumerable<LogEntry> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(SubscriberQueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        });
        lock (_lock)
        {
            _subscribers.Add(channel.Writer);
        }
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                if (!more)
                {
                    yield break;
                }
                while (channel.Reader.TryRead(out LogEntry? entry))
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel.Writer);
            }
        }
    }
}
