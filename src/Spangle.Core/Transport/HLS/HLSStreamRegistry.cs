using System.Collections.Concurrent;

namespace Spangle.Transport.HLS;

/// <summary>
/// In-memory registry of live HLS playlists, keyed by (sanitized) stream name.
/// Producers publish playlist updates; the HTTP layer serves them and implements
/// LL-HLS blocking reload (<c>_HLS_msn</c>/<c>_HLS_part</c>) by awaiting updates.
/// </summary>
public sealed class HLSStreamRegistry
{
    private readonly ConcurrentDictionary<string, LivePlaylist> _streams = new();
    private readonly ConcurrentDictionary<string, HLSPlaylistHandover> _handovers = new();

    /// <summary>Stashes the live-playlist state of a kicked session for its successor.</summary>
    internal void StashHandover(string streamKey, HLSPlaylistHandover state) => _handovers[streamKey] = state;

    /// <summary>Takes (and removes) the handover state left by a kicked predecessor, if any.</summary>
    internal HLSPlaylistHandover? TakeHandover(string streamKey) =>
        _handovers.TryRemove(streamKey, out HLSPlaylistHandover? state) ? state : null;

    public LivePlaylist GetOrAdd(string streamKey) =>
        _streams.GetOrAdd(streamKey, static _ => new LivePlaylist());

    public bool TryGet(string streamKey, out LivePlaylist playlist) =>
        _streams.TryGetValue(streamKey, out playlist!);

    public void Remove(string streamKey) => _streams.TryRemove(streamKey, out _);

    public sealed class LivePlaylist
    {
        private readonly Lock _lock = new();
        private string _text = "";
        private string? _delta;
        private long _msn = -1;
        private int _part = -1;
        private TaskCompletionSource _next = NewTcs();

        private static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Publishes a new playlist state and wakes all blocked readers.
        /// <paramref name="delta"/> is the EXT-X-SKIP variant, when one exists.
        /// </summary>
        public void Publish(string text, string? delta, long msn, int part)
        {
            TaskCompletionSource previous;
            lock (_lock)
            {
                _text = text;
                _delta = delta;
                _msn = msn;
                _part = part;
                previous = _next;
                _next = NewTcs();
            }
            previous.SetResult();
        }

        /// <summary>
        /// Returns the playlist, blocking until the given media sequence number / part
        /// is available (LL-HLS blocking reload) or the timeout elapses. With
        /// <paramref name="skip"/> (`_HLS_skip`), the delta update is preferred when
        /// one exists — the full playlist is always a valid answer otherwise.
        /// </summary>
        public async ValueTask<string> WaitForAsync(long msn, int part, bool skip, TimeSpan timeout,
            CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Task waitTask;
                lock (_lock)
                {
                    if (_msn > msn || (_msn == msn && _part >= part))
                    {
                        return Pick(skip);
                    }
                    waitTask = _next.Task;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    lock (_lock)
                    {
                        return Pick(skip); // give up blocking; serve the current state
                    }
                }
                try
                {
                    await waitTask.WaitAsync(remaining, ct);
                }
                catch (TimeoutException)
                {
                    lock (_lock)
                    {
                        return Pick(skip);
                    }
                }
            }
        }

        public string GetCurrent(bool skip)
        {
            lock (_lock)
            {
                return Pick(skip);
            }
        }

        public string Current => GetCurrent(skip: false);

        private string Pick(bool skip) => skip ? _delta ?? _text : _text;

        /// <summary>The media sequence number of the newest published playlist state.</summary>
        public long CurrentMsn
        {
            get
            {
                lock (_lock)
                {
                    return _msn;
                }
            }
        }
    }
}
