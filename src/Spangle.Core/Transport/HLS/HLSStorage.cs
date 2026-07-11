using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// Where HLS output lives. The senders write segments, parts, init files and
/// playlists through this abstraction; the HTTP layer reads through it.
/// Backends: <see cref="MemoryHLSStorage"/> (live serving without touching disk)
/// and <see cref="FileHLSStorage"/> (output doubles as an archive and is
/// servable by any static file server).
/// </summary>
public interface IHLSStorage
{
    /// <summary>Gets (creating if needed) the storage area of one stream.</summary>
    IHLSStreamStorage GetStream(string streamKey);

    /// <summary>Read-side lookup for HTTP serving; false when the stream never produced output.</summary>
    bool TryGetStream(string streamKey, out IHLSStreamStorage stream);
}

/// <summary>
/// Optional capability of a stream storage: blobs that can be read while still being
/// written (LL-DASH serves in-progress segments over chunked transfer). By design
/// only the memory backend implements this — a future DVR would serve from memory
/// while archiving to files, never the other way around.
/// </summary>
public interface ILiveBlobStreamStorage
{
    /// <summary>Appends to a growing blob, creating it on the first call.</summary>
    void AppendBlob(string name, ReadOnlySpan<byte> content);

    /// <summary>Seals a growing blob; it becomes a regular blob afterwards.</summary>
    void CompleteBlob(string name);

    /// <summary>Opens a reader over a blob still being written; false when none grows under the name.</summary>
    bool TryOpenLiveBlob(string name, out LiveBlobReader reader);
}

/// <summary>
/// Sequential reader over a growing blob: yields the chunks written so far, then
/// awaits future appends, and reports null once the blob is completed.
/// </summary>
public sealed class LiveBlobReader
{
    private readonly Func<int, (byte[]? Chunk, bool Completed, Task Wait)> _read;
    private int _index;

    internal LiveBlobReader(Func<int, (byte[]?, bool, Task)> read)
    {
        _read = read;
    }

    /// <summary>The next chunk, or null when the blob is complete.</summary>
    public async ValueTask<byte[]?> ReadNextAsync(CancellationToken ct)
    {
        while (true)
        {
            (byte[]? chunk, bool completed, Task wait) = _read(_index);
            if (chunk is not null)
            {
                _index++;
                return chunk;
            }
            if (completed)
            {
                return null;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>The storage area of a single stream (one playlist and its media blobs).</summary>
public interface IHLSStreamStorage
{
    /// <summary>Stores a media blob (segment, partial segment, or init file) under <paramref name="name"/>.</summary>
    void WriteBlob(string name, ReadOnlySpan<byte> content);

    /// <summary>Removes a blob that fell out of the live window. Missing blobs are ignored.</summary>
    void DeleteBlob(string name);

    /// <summary>Atomically replaces the playlist.</summary>
    void PublishPlaylist(string text);

    bool TryReadBlob(string name, out ReadOnlyMemory<byte> content);

    /// <summary>The current playlist text; null when none was published yet.</summary>
    string? Playlist { get; }
}

/// <summary>
/// Keeps the live window in process memory. Blobs the playlist trims are freed, so
/// a stream holds roughly its sliding window (a few MB); after the stream ends its
/// final window stays servable until the same stream key publishes again.
/// </summary>
public sealed class MemoryHLSStorage : IHLSStorage
{
    private readonly ConcurrentDictionary<string, IHLSStreamStorage> _streams = new(StringComparer.Ordinal);

    public IHLSStreamStorage GetStream(string streamKey) =>
        _streams.GetOrAdd(streamKey, static _ => new StreamStorage());

    public bool TryGetStream(string streamKey, out IHLSStreamStorage stream) =>
        _streams.TryGetValue(streamKey, out stream!);

    public override string ToString() => "memory";

    private sealed class StreamStorage : IHLSStreamStorage, ILiveBlobStreamStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, GrowingBlob> _growing = new(StringComparer.Ordinal);
        private volatile string? _playlist;

        public void WriteBlob(string name, ReadOnlySpan<byte> content) => _blobs[name] = content.ToArray();

        public void DeleteBlob(string name)
        {
            _blobs.TryRemove(name, out _);
            if (_growing.TryRemove(name, out GrowingBlob? orphan))
            {
                orphan.Complete(); // release any blocked readers
            }
        }

        public void PublishPlaylist(string text) => _playlist = text;

        public bool TryReadBlob(string name, out ReadOnlyMemory<byte> content)
        {
            if (_blobs.TryGetValue(name, out byte[]? blob))
            {
                content = blob;
                return true;
            }
            content = default;
            return false;
        }

        public string? Playlist => _playlist;

        // ---- growing blobs (LL-DASH) ----

        public void AppendBlob(string name, ReadOnlySpan<byte> content) =>
            _growing.GetOrAdd(name, static _ => new GrowingBlob()).Append(content);

        public void CompleteBlob(string name)
        {
            if (_growing.TryRemove(name, out GrowingBlob? blob))
            {
                // seal first so late readers of the growing handle terminate too
                _blobs[name] = blob.ToArray();
                blob.Complete();
            }
        }

        public bool TryOpenLiveBlob(string name, out LiveBlobReader reader)
        {
            if (_growing.TryGetValue(name, out GrowingBlob? blob))
            {
                reader = blob.OpenReader();
                return true;
            }
            reader = null!;
            return false;
        }

        private sealed class GrowingBlob
        {
            private readonly Lock _lock = new();
            private readonly List<byte[]> _chunks = new();
            private bool _completed;
            private TaskCompletionSource _next = NewTcs();

            private static TaskCompletionSource NewTcs() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Append(ReadOnlySpan<byte> content)
            {
                TaskCompletionSource previous;
                lock (_lock)
                {
                    _chunks.Add(content.ToArray());
                    previous = _next;
                    _next = NewTcs();
                }
                previous.SetResult();
            }

            public void Complete()
            {
                TaskCompletionSource previous;
                lock (_lock)
                {
                    _completed = true;
                    previous = _next;
                }
                previous.TrySetResult();
            }

            public byte[] ToArray()
            {
                lock (_lock)
                {
                    var total = 0;
                    foreach (byte[] chunk in _chunks)
                    {
                        total += chunk.Length;
                    }
                    var all = new byte[total];
                    var pos = 0;
                    foreach (byte[] chunk in _chunks)
                    {
                        chunk.CopyTo(all, pos);
                        pos += chunk.Length;
                    }
                    return all;
                }
            }

            public LiveBlobReader OpenReader() => new(index =>
            {
                lock (_lock)
                {
                    if (index < _chunks.Count)
                    {
                        return (_chunks[index], false, Task.CompletedTask);
                    }
                    return (null, _completed, _next.Task);
                }
            });
        }
    }
}

/// <summary>
/// Writes each stream into a subdirectory of <paramref name="rootDirectory"/> —
/// the pre-storage behavior: inspectable with regular tools, persists as an
/// archive after the stream ends, servable by static file middleware.
/// </summary>
public sealed class FileHLSStorage(string rootDirectory) : IHLSStorage
{
    private readonly ConcurrentDictionary<string, IHLSStreamStorage> _streams = new(StringComparer.Ordinal);

    public IHLSStreamStorage GetStream(string streamKey) =>
        _streams.GetOrAdd(streamKey, key =>
        {
            string directory = Path.Combine(rootDirectory, key);
            Directory.CreateDirectory(directory);
            return new StreamStorage(directory);
        });

    public bool TryGetStream(string streamKey, out IHLSStreamStorage stream)
    {
        if (_streams.TryGetValue(streamKey, out stream!))
        {
            return true;
        }
        // e.g. archives from a previous server run
        if (Directory.Exists(Path.Combine(rootDirectory, streamKey)))
        {
            stream = GetStream(streamKey);
            return true;
        }
        return false;
    }

    public override string ToString() => $"file:{rootDirectory}";

    private sealed class StreamStorage(string directory) : IHLSStreamStorage
    {
        private static readonly ILogger<FileHLSStorage> s_logger = SpangleLogManager.GetLogger<FileHLSStorage>();

        public void WriteBlob(string name, ReadOnlySpan<byte> content)
        {
            using var file = File.Create(Path.Combine(directory, name));
            file.Write(content);
        }

        public void DeleteBlob(string name)
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (IOException e)
            {
                s_logger.ZLogWarning($"Failed to delete old file {name}: {e.Message}");
            }
        }

        public void PublishPlaylist(string text) =>
            File.WriteAllText(Path.Combine(directory, "playlist.m3u8"), text);

        public bool TryReadBlob(string name, out ReadOnlyMemory<byte> content)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllBytes(path);
                    return true;
                }
                catch (IOException)
                {
                    // deleted or locked between the check and the read
                }
            }
            content = default;
            return false;
        }

        public string? Playlist
        {
            get
            {
                string path = Path.Combine(directory, "playlist.m3u8");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
        }
    }
}
