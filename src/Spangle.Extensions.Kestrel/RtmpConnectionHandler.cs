using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Spangle.Transport.HLS;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Handles one RTMP publisher connection: receives via RTMP and writes HLS output
/// into the configured directory (served by the HTTP pipeline).
/// </summary>
public class RtmpConnectionHandler(
    IOptions<SpangleMediaServerOptions> options,
    IHostApplicationLifetime lifetime,
    HLSStreamRegistry registry,
    PublishSessionRegistry publishSessions,
    IPublishAuthorizer publishAuthorizer,
    IHLSStorage storage,
    Spangle.Spinner.TimedMetadataHub metadataHub,
    ILogger<RtmpConnectionHandler> logger) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            connection.ConnectionClosed, lifetime.ApplicationStopping);
        var ct = cts.Token;

        var receiver = connection.CreateRtmpReceiverContext(ct);
        receiver.Bandwidth = options.Value.Rtmp.Bandwidth;
        var hlsOptions = options.Value.Hls;
        var segmentFormat = hlsOptions.SegmentFormat.ToLowerInvariant() switch
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
        // DI-composed pipeline plugins: AMF0 data events -> ID3 conversion, and the
        // side door for server-injected metadata (POST /api/streams/{key}/metadata)
        var spinners = new List<Spangle.Spinner.ISpinner>(2);
        if (options.Value.Rtmp.TimedMetadata)
        {
            spinners.Add(new Spangle.Spinner.AmfDataToId3Spinner(ct));
        }
        if (options.Value.Http.MetadataInjection)
        {
            spinners.Add(new Spangle.Spinner.TimedMetadataInjector(receiver, metadataHub, ct));
        }
        using var live = new LiveContext(receiver, hls, mediaSpinners: spinners,
            publishSessions: publishSessions, publishAuthorizer: publishAuthorizer,
            audioOnlyFallback: options.Value.Rtmp.AudioOnlyFallbackMs > 0
                ? TimeSpan.FromMilliseconds(options.Value.Rtmp.AudioOnlyFallbackMs)
                : null,
            cancellationToken: ct);
        ISender<HLSSenderContext> sender = segmentFormat == HLSSegmentFormat.Fmp4
            ? new CmafHLSSender()
            : new HLSSender();

        var senderTask = Task.Run(async () =>
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
            logger.ZLogInformation($"RTMP connection opened: {receiver.ToString()}");
            await live.StartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal at client disconnect or server shutdown
        }
        catch (ConnectionResetException)
        {
            // A publisher crashing or losing its network is routine for a media server;
            // the tail of the stream has already been drained into the HLS output.
            logger.ZLogInformation($"RTMP publisher disconnected abruptly: {receiver.ToString()}");
        }
        catch (Exception e)
        {
            logger.ZLogError($"RTMP receiver error: {e}");
        }
        finally
        {
            // The completion propagates receiver -> spinner -> sender; wait for the playlist to be finalized
            await senderTask.ConfigureAwait(false);
            (sender as IDisposable)?.Dispose();
            logger.ZLogInformation($"RTMP connection closed: {receiver.ToString()}");
        }
    }
}
