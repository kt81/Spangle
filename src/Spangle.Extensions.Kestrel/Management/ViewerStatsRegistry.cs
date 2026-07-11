using System.Collections.Concurrent;

namespace Spangle.Extensions.Kestrel.Management;

/// <summary>
/// Delivery-side counters per stream key: playlist fetches and currently blocked
/// LL-HLS reloads. Updated from the HTTP hot path, so everything is lock-free.
/// </summary>
public sealed class ViewerStatsRegistry
{
    private static readonly TimeSpan s_idleEviction = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public long PlaylistRequests;
        public int ActiveWaiters;
        public long LastTouchTicks = Environment.TickCount64;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _pruneCounter;

    public void OnPlaylistRequest(string streamKey)
    {
        Entry entry = Touch(streamKey);
        Interlocked.Increment(ref entry.PlaylistRequests);
        if (Interlocked.Increment(ref _pruneCounter) % 1024 == 0)
        {
            Prune();
        }
    }

    public void WaiterEntered(string streamKey) => Interlocked.Increment(ref Touch(streamKey).ActiveWaiters);

    public void WaiterExited(string streamKey) => Interlocked.Decrement(ref Touch(streamKey).ActiveWaiters);

    public (long PlaylistRequests, int ActiveWaiters) Get(string streamKey) =>
        _entries.TryGetValue(streamKey, out Entry? entry)
            ? (Volatile.Read(ref entry.PlaylistRequests), Volatile.Read(ref entry.ActiveWaiters))
            : (0, 0);

    private Entry Touch(string streamKey)
    {
        Entry entry = _entries.GetOrAdd(streamKey, static _ => new Entry());
        Volatile.Write(ref entry.LastTouchTicks, Environment.TickCount64);
        return entry;
    }

    private void Prune()
    {
        long now = Environment.TickCount64;
        foreach ((string key, Entry entry) in _entries)
        {
            if (Volatile.Read(ref entry.ActiveWaiters) == 0
                && now - Volatile.Read(ref entry.LastTouchTicks) > s_idleEviction.TotalMilliseconds)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }
}
