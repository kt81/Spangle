using System.Collections.Concurrent;

namespace Spangle.Extensions.Kestrel.Management;

/// <summary>One stream's live stats as served by the management API.</summary>
public sealed record StreamStatsDto
{
    public required string StreamKey { get; init; }
    public required string StreamName { get; init; }
    public required string Protocol { get; init; }
    public required string RemoteEndPoint { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double UptimeSeconds { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public bool IsAudioOnly { get; init; }
    public long BytesReceived { get; init; }

    /// <summary>Ingest bitrate over the interval since the previous poll; 0 on the first sample</summary>
    public long IngestBitrateBps { get; init; }

    public long PlaylistRequests { get; init; }
    public double PlaylistRequestsPerMinute { get; init; }

    /// <summary>LL-HLS blocking reloads currently parked on this stream (a lower bound of live players)</summary>
    public int ActiveWaiters { get; init; }
}

/// <summary>
/// Joins the publish sessions with delivery counters and derives the rates
/// (ingest bitrate, playlist requests/min) from deltas between polls.
/// </summary>
public sealed class ManagementStatsService(PublishSessionRegistry sessions, ViewerStatsRegistry viewers)
{
    private sealed record Sample(long Bytes, long PlaylistRequests, long Ticks);

    private readonly ConcurrentDictionary<string, Sample> _lastSamples = new(StringComparer.Ordinal);

    public IReadOnlyList<StreamStatsDto> Collect()
    {
        IReadOnlyList<PublishSessionInfo> live = sessions.ListSessions();
        long now = Environment.TickCount64;
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;

        var result = new List<StreamStatsDto>(live.Count);
        var seen = new HashSet<string>(live.Count, StringComparer.Ordinal);
        foreach (PublishSessionInfo info in live)
        {
            (long requests, int waiters) = viewers.Get(info.StreamKey);

            string sampleKey = $"{info.StreamKey}\n{info.SessionId}";
            seen.Add(sampleKey);
            long bitrate = 0;
            double requestsPerMinute = 0;
            if (_lastSamples.TryGetValue(sampleKey, out Sample? last) && now > last.Ticks)
            {
                double elapsedSec = (now - last.Ticks) / 1000.0;
                bitrate = (long)((info.BytesReceived - last.Bytes) * 8 / elapsedSec);
                requestsPerMinute = (requests - last.PlaylistRequests) / elapsedSec * 60;
            }
            _lastSamples[sampleKey] = new Sample(info.BytesReceived, requests, now);

            result.Add(new StreamStatsDto
            {
                StreamKey = info.StreamKey,
                StreamName = info.StreamName,
                Protocol = info.Protocol,
                RemoteEndPoint = info.RemoteEndPoint,
                StartedAt = info.StartedAt,
                UptimeSeconds = (utcNow - info.StartedAt).TotalSeconds,
                VideoCodec = info.VideoCodec?.ToString(),
                AudioCodec = info.AudioCodec?.ToString(),
                Width = info.VideoWidth,
                Height = info.VideoHeight,
                IsAudioOnly = info.IsAudioOnly,
                BytesReceived = info.BytesReceived,
                IngestBitrateBps = bitrate < 0 ? 0 : bitrate,
                PlaylistRequests = requests,
                PlaylistRequestsPerMinute = requestsPerMinute < 0 ? 0 : Math.Round(requestsPerMinute, 1),
                ActiveWaiters = waiters,
            });
        }

        // ended sessions must not leak samples
        foreach (string key in _lastSamples.Keys)
        {
            if (!seen.Contains(key))
            {
                _lastSamples.TryRemove(key, out _);
            }
        }

        return result;
    }
}
