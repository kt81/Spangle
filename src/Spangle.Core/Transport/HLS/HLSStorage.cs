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
    private readonly ConcurrentDictionary<string, IHLSStreamStorage> _streams = new();

    public IHLSStreamStorage GetStream(string streamKey) =>
        _streams.GetOrAdd(streamKey, static _ => new StreamStorage());

    public bool TryGetStream(string streamKey, out IHLSStreamStorage stream) =>
        _streams.TryGetValue(streamKey, out stream!);

    public override string ToString() => "memory";

    private sealed class StreamStorage : IHLSStreamStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
        private volatile string? _playlist;

        public void WriteBlob(string name, ReadOnlySpan<byte> content) => _blobs[name] = content.ToArray();

        public void DeleteBlob(string name) => _blobs.TryRemove(name, out _);

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
    }
}

/// <summary>
/// Writes each stream into a subdirectory of <paramref name="rootDirectory"/> —
/// the pre-storage behavior: inspectable with regular tools, persists as an
/// archive after the stream ends, servable by static file middleware.
/// </summary>
public sealed class FileHLSStorage(string rootDirectory) : IHLSStorage
{
    private readonly ConcurrentDictionary<string, IHLSStreamStorage> _streams = new();

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
