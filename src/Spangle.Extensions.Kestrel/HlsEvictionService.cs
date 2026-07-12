using Microsoft.Extensions.Options;
using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Frees ended streams from evicting storage backends (memory) once they have been
/// idle for <c>Hls.EndedStreamTtlSeconds</c>. Without this, every distinct stream key
/// ever published would hold its final window in memory for the process lifetime.
/// </summary>
public sealed class HlsEvictionService(
    IHLSStorage storage,
    PublishSessionRegistry sessions,
    IOptions<SpangleMediaServerOptions> options,
    ILogger<HlsEvictionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int ttlSeconds = options.Value.Hls.EndedStreamTtlSeconds;
        if (ttlSeconds <= 0 || storage is not IEvictingHLSStorage evicting)
        {
            return; // eviction disabled, or the backend (file archive) never evicts
        }

        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        // sweeping a few times per TTL keeps the overshoot small without busy-looping
        var period = TimeSpan.FromSeconds(Math.Clamp(ttlSeconds / 4.0, 5.0, 60.0));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            int evicted = evicting.EvictIdleStreams(ttl, sessions.IsLive);
            if (evicted > 0)
            {
                logger.ZLogInformation($"Freed {evicted} ended stream(s) idle for {ttl}");
            }
        }
    }
}
