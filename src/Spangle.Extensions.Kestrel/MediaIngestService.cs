using Microsoft.Extensions.Options;
using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Runs every registered pull ingest source (from any <see cref="IIngestSourceProvider"/>) on its
/// own reconnect loop, republishing each through the host's HLS/CMAF output. This is the generic
/// host side of ingest — it knows how to dial nothing itself; a provider (MOQT, or another) supplies
/// the sources, and this owns the loop and the HLS wiring, so the host never references the
/// protocol stack behind a source.
/// </summary>
public sealed class MediaIngestService(
    IOptions<SpangleMediaServerOptions> options,
    IEnumerable<IIngestSourceProvider> providers,
    IHLSStorage storage,
    HLSStreamRegistry registry,
    PublishSessionRegistry publishSessions,
    IPublishAuthorizer publishAuthorizer,
    IEnumerable<IPublishEgressFactory> egressFactories,
    ILogger<MediaIngestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<IIngestSource> sources = [.. providers.SelectMany(p => p.Sources)];
        if (sources.Count == 0)
        {
            return;
        }

        logger.ZLogInformation($"Media ingest: pulling {sources.Count} source(s).");
        await Task.WhenAll(sources.Select(source => RunSourceAsync(source, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunSourceAsync(IIngestSource source, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IIngestConnection connection = await source.ConnectAsync(ct).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    await HlsIngestPipeline.RunAsync(connection.Receiver, options.Value.Hls, storage, registry,
                        egressFactories, publishSessions, publishAuthorizer, logger, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.ZLogWarning($"Media ingest source `{source.Name}` error: {e.Message}");
            }

            try
            {
                await Task.Delay(source.ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
