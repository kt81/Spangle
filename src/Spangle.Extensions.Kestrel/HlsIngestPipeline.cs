using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Wires one receiver to the host's HLS/CMAF output and runs the session to completion — the shared
/// tail every ingest path ends in, whichever protocol produced the MediaFrames. A pull ingest (RTSP
/// or MOQT) hands its receiver here; the push handlers keep their own copy because they interleave
/// it with connection-specific concerns.
/// </summary>
internal static class HlsIngestPipeline
{
    public static async Task RunAsync(IReceiverContext receiver, HlsOptions hlsOptions, IHLSStorage storage,
        HLSStreamRegistry registry, IEnumerable<IPublishEgressFactory> egressFactories,
        PublishSessionRegistry publishSessions, IPublishAuthorizer publishAuthorizer, ILogger logger,
        CancellationToken ct)
    {
        HLSSegmentFormat segmentFormat = hlsOptions.SegmentFormat.ToLowerInvariant() switch
        {
            "fmp4" or "cmaf" or "mp4" => HLSSegmentFormat.Fmp4,
            _ => HLSSegmentFormat.MpegTs,
        };
        var hls = new HLSSenderContext(ct)
        {
            Storage = storage,
            OutputDirectory = hlsOptions.OutputDirectory,
            TargetSegmentDuration = hlsOptions.TargetSegmentDuration,
            PlaylistWindow = hlsOptions.PlaylistWindow,
            SegmentFormat = segmentFormat,
            LowLatency = hlsOptions.LowLatency && segmentFormat == HLSSegmentFormat.Fmp4,
            PartTargetDuration = hlsOptions.PartTargetDuration,
            Registry = registry,
        };
        // Additional egresses (e.g. MOQT) ride the same MediaFrame stream through a fan-out — so a
        // pulled source can itself be re-published to MOQT, the same as a locally-ingested one.
        var egresses = SessionEgresses.Start(egressFactories, ct);
        using var live = new LiveContext(receiver, hls,
            publishSessions: publishSessions, publishAuthorizer: publishAuthorizer,
            additionalSenders: egresses.Senders, cancellationToken: ct);
        ISender<HLSSenderContext> sender = segmentFormat == HLSSegmentFormat.Fmp4
            ? new CmafHLSSender()
            : new HLSSender();

        Task senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.StartAsync(hls).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.ZLogError($"HLS sender error: {e}");
            }
        }, CancellationToken.None);

        try
        {
            await live.StartAsync().ConfigureAwait(false);
        }
        finally
        {
            await senderTask.ConfigureAwait(false);
            (sender as IDisposable)?.Dispose();
            await egresses.DisposeAsync().ConfigureAwait(false);
        }
    }
}
