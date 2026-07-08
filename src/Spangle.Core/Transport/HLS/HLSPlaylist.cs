using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// Maintains a live M3U8 media playlist with a sliding window:
/// segment naming, old segment deletion, and playlist (re)writing.
/// </summary>
internal sealed class HLSPlaylist(string directory, string? mapUri = null)
{
    private static readonly ILogger<HLSPlaylist> s_logger = SpangleLogManager.GetLogger<HLSPlaylist>();

    private const int WindowSize = 6;

    private readonly List<(string Name, double Duration)> _window = new();
    private int _sequence;

    /// <summary>The file name the next segment should be written as</summary>
    public string NextSegmentName(string extension) => $"seg{_sequence:D5}{extension}";

    /// <summary>Registers a written segment and rewrites the playlist</summary>
    public void AddSegment(string name, double duration)
    {
        _sequence++;
        _window.Add((name, duration));
        while (_window.Count > WindowSize)
        {
            (string Name, double) oldest = _window[0];
            _window.RemoveAt(0);
            try
            {
                File.Delete(Path.Combine(directory, oldest.Name));
            }
            catch (IOException e)
            {
                s_logger.ZLogWarning($"Failed to delete old segment {oldest.Name}: {e.Message}");
            }
        }

        Write(ended: false);
        s_logger.ZLogDebug($"Segment written: {name} ({duration:F3}s)");
    }

    /// <summary>Marks the playlist as ended (VOD-style tail)</summary>
    public void Complete()
    {
        Write(ended: true);
    }

    private void Write(bool ended)
    {
        if (_window.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append(mapUri is null ? "#EXT-X-VERSION:3\n" : "#EXT-X-VERSION:6\n");
        sb.Append($"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(_window.Max(static w => w.Duration))}\n");
        sb.Append($"#EXT-X-MEDIA-SEQUENCE:{_sequence - _window.Count}\n");
        if (mapUri is not null)
        {
            sb.Append($"#EXT-X-MAP:URI=\"{mapUri}\"\n");
        }
        foreach ((string name, double duration) in _window)
        {
            sb.Append($"#EXTINF:{duration:F3},\n");
            sb.Append(name).Append('\n');
        }
        if (ended)
        {
            sb.Append("#EXT-X-ENDLIST\n");
        }

        File.WriteAllText(Path.Combine(directory, "playlist.m3u8"), sb.ToString());
    }
}
