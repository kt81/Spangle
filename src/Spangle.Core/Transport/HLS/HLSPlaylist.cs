using System.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// The live-playlist state a kicked session hands to its successor (takeover):
/// the successor continues the media sequence and the sliding window.
/// </summary>
internal sealed record HLSPlaylistHandover(int Sequence, IReadOnlyList<HLSPlaylist.SegmentEntry> Window);

/// <summary>
/// Maintains a live M3U8 media playlist with a sliding window:
/// segment naming, old file deletion, playlist (re)writing, and optionally
/// LL-HLS partial segments (EXT-X-PART / PART-INF / SERVER-CONTROL / PRELOAD-HINT).
/// </summary>
internal sealed class HLSPlaylist
{
    private static readonly ILogger<HLSPlaylist> s_logger = SpangleLogManager.GetLogger<HLSPlaylist>();

    /// <summary>
    /// RFC 8216bis 6.2.5.1: a delta must retain everything within CAN-SKIP-UNTIL of
    /// the playlist end, and CAN-SKIP-UNTIL must be at least 6 target durations.
    /// </summary>
    private const int SkipUntilTargetDurations = 6;

    internal readonly record struct SegmentEntry(string Name, double Duration, bool Discontinuity);

    private readonly IHLSStreamStorage _storage;
    private readonly string? _mapUri;
    private readonly double? _partTargetDuration;
    private readonly int _windowSize;
    private readonly Action<string, string?, long, int>? _onUpdated;

    private readonly List<SegmentEntry> _window = new();
    private readonly List<(string Name, double Duration, bool Independent)> _currentParts = new();
    private List<(string Name, double Duration, bool Independent)> _lastSegmentParts = new();
    private readonly StringBuilder _sb = new(1024);
    private int _sequence;
    private bool _pendingDiscontinuity;
    private int _targetDurationCeil;

    public HLSPlaylist(IHLSStreamStorage storage, string? mapUri = null, double? partTargetDuration = null,
        Action<string, string?, long, int>? onUpdated = null, HLSPlaylistHandover? resume = null,
        int windowSize = 6)
    {
        _storage = storage;
        _mapUri = mapUri;
        _partTargetDuration = partTargetDuration;
        _windowSize = windowSize;
        _onUpdated = onUpdated;

        if (resume is not null)
        {
            // Takeover: continue the predecessor's media sequence and window, and
            // mark the timeline break so players expect a timestamp jump.
            _sequence = resume.Sequence;
            _window.AddRange(resume.Window);
            _pendingDiscontinuity = true;
            s_logger.ZLogInformation($"Resuming playlist at media sequence {_sequence} (takeover)");
        }
    }

    /// <summary>Snapshot for handing the live playlist over to a successor session.</summary>
    public HLSPlaylistHandover ExportHandover() => new(_sequence, _window.ToArray());

    /// <summary>The media sequence number of the segment currently being produced</summary>
    public long CurrentMsn => _sequence;

    /// <summary>The file name the next segment should be written as</summary>
    public string NextSegmentName(string extension) => $"seg{_sequence:D5}{extension}";

    /// <summary>The file name the next partial segment should be written as</summary>
    public string NextPartName() => PartName(_currentParts.Count);

    private string PartName(int index) => $"seg{_sequence:D5}.p{index:D2}.m4s";

    /// <summary>Registers a written partial segment and republishes the playlist</summary>
    public void AddPart(string name, double duration, bool independent)
    {
        _currentParts.Add((name, duration, independent));
        Write(ended: false);
    }

    /// <summary>Registers a written segment and rewrites the playlist</summary>
    public void AddSegment(string name, double duration)
    {
        // Parts two generations old fall out of the playlist; delete their files
        foreach ((string partName, _, _) in _lastSegmentParts)
        {
            TryDelete(partName);
        }
        _lastSegmentParts = new List<(string, double, bool)>(_currentParts);
        _currentParts.Clear();

        _sequence++;
        _window.Add(new SegmentEntry(name, duration, _pendingDiscontinuity));
        _pendingDiscontinuity = false;
        while (_window.Count > _windowSize)
        {
            SegmentEntry oldest = _window[0];
            _window.RemoveAt(0);
            TryDelete(oldest.Name);
        }

        Write(ended: false);
        s_logger.ZLogDebug($"Segment written: {name} ({duration:F3}s)");
    }

    /// <summary>Marks the playlist as ended (VOD-style tail)</summary>
    public void Complete()
    {
        Write(ended: true);
    }

    private void TryDelete(string name) => _storage.DeleteBlob(name);

    private void Write(bool ended)
    {
        if (_window.Count == 0 && _currentParts.Count == 0)
        {
            return;
        }

        // RFC 8216 6.2.1: EXT-X-TARGETDURATION MUST NOT change, so track the ceiling
        // monotonically instead of recomputing it from the sliding window (where a
        // long segment leaving the window would shrink the value again)
        double windowMax = _window.Count > 0 ? _window.Max(static w => w.Duration) : _partTargetDuration ?? 1.0;
        _targetDurationCeil = Math.Max(_targetDurationCeil, (int)Math.Ceiling(windowMax));

        var text = Render(ended, skipCount: 0);
        string? delta = RenderDelta(ended);
        _storage.PublishPlaylist(text);
        _onUpdated?.Invoke(text, delta, CurrentMsn, _currentParts.Count - 1);
    }

    /// <summary>
    /// The delta-update variant (`?_HLS_skip=YES`): everything older than
    /// CAN-SKIP-UNTIL collapses into one EXT-X-SKIP line. Null when there is nothing
    /// to skip, in a non-LL playlist, or when a discontinuity falls into the skipped
    /// range (skipping across one needs DISCONTINUITY-SEQUENCE bookkeeping we don't
    /// track — the full playlist is always a valid response).
    /// </summary>
    private string? RenderDelta(bool ended)
    {
        if (_partTargetDuration is null || _window.Count == 0)
        {
            return null;
        }

        double keep = SkipUntilTargetDurations * (double)_targetDurationCeil;
        double tail = 0;
        int firstKept = _window.Count;
        while (firstKept > 0 && tail < keep)
        {
            firstKept--;
            tail += _window[firstKept].Duration;
        }
        if (firstKept == 0)
        {
            return null; // the whole window is within the keep range
        }
        for (var i = 0; i <= firstKept; i++)
        {
            // a discontinuity inside (or right at the edge of) the skipped range
            if (_window[i].Discontinuity)
            {
                return null;
            }
        }
        return Render(ended, firstKept);
    }

    private string Render(bool ended, int skipCount)
    {
        StringBuilder sb = _sb.Clear();
        sb.Append("#EXTM3U\n");
        // EXT-X-SKIP requires protocol version 9
        sb.Append(skipCount > 0 ? "#EXT-X-VERSION:9\n" : _mapUri is null ? "#EXT-X-VERSION:3\n" : "#EXT-X-VERSION:6\n");
        sb.Append($"#EXT-X-TARGETDURATION:{_targetDurationCeil}\n");
        if (_partTargetDuration is { } partTarget)
        {
            sb.Append($"#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK={partTarget * 3:F3}");
            sb.Append($",CAN-SKIP-UNTIL={SkipUntilTargetDurations * (double)_targetDurationCeil:F1}\n");
            sb.Append($"#EXT-X-PART-INF:PART-TARGET={partTarget:F3}\n");
        }
        sb.Append($"#EXT-X-MEDIA-SEQUENCE:{_sequence - _window.Count}\n");
        if (_mapUri is not null)
        {
            sb.Append($"#EXT-X-MAP:URI=\"{_mapUri}\"\n");
        }
        if (skipCount > 0)
        {
            sb.Append($"#EXT-X-SKIP:SKIPPED-SEGMENTS={skipCount}\n");
        }

        for (int i = skipCount; i < _window.Count; i++)
        {
            SegmentEntry entry = _window[i];
            if (entry.Discontinuity)
            {
                sb.Append("#EXT-X-DISCONTINUITY\n");
            }
            if (_partTargetDuration is not null && i == _window.Count - 1)
            {
                // Keep the parts of the most recent completed segment available
                AppendParts(sb, _lastSegmentParts);
            }
            sb.Append($"#EXTINF:{entry.Duration:F3},\n");
            sb.Append(entry.Name).Append('\n');
        }

        if (_partTargetDuration is not null)
        {
            AppendParts(sb, _currentParts);
            if (!ended)
            {
                sb.Append($"#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"{NextPartName()}\"\n");
            }
        }

        if (ended)
        {
            sb.Append("#EXT-X-ENDLIST\n");
        }

        return sb.ToString();
    }

    private static void AppendParts(StringBuilder sb, List<(string Name, double Duration, bool Independent)> parts)
    {
        foreach ((string name, double duration, bool independent) in parts)
        {
            sb.Append($"#EXT-X-PART:DURATION={duration:F3},URI=\"{name}\"");
            if (independent)
            {
                sb.Append(",INDEPENDENT=YES");
            }
            sb.Append('\n');
        }
    }
}
