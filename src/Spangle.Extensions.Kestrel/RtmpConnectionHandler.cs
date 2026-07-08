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
    ILogger<RtmpConnectionHandler> logger) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            connection.ConnectionClosed, lifetime.ApplicationStopping);
        var ct = cts.Token;

        var receiver = connection.CreateRtmpReceiverContext(ct);
        var hlsOptions = options.Value.Hls;
        var segmentFormat = hlsOptions.SegmentFormat.ToLowerInvariant() switch
        {
            "fmp4" or "cmaf" or "mp4" => HLSSegmentFormat.Fmp4,
            _ => HLSSegmentFormat.MpegTs,
        };
        var hls = new HLSSenderContext(ct)
        {
            OutputDirectory = hlsOptions.OutputDirectory,
            TargetSegmentDuration = hlsOptions.TargetSegmentDuration,
            SegmentFormat = segmentFormat,
        };
        using var live = new LiveContext(receiver, hls, ct);
        ISender<HLSSenderContext> sender = segmentFormat == HLSSegmentFormat.Fmp4
            ? new CmafHLSSender()
            : new HLSSender();

        var senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.StartAsync(hls);
            }
            catch (Exception e)
            {
                logger.ZLogError($"HLS sender error: {e}");
            }
        }, CancellationToken.None);

        try
        {
            logger.ZLogInformation($"RTMP connection opened: {receiver.ToString()}");
            await live.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal at client disconnect or server shutdown
        }
        catch (Exception e)
        {
            logger.ZLogError($"RTMP receiver error: {e}");
        }
        finally
        {
            // The completion propagates receiver -> spinner -> sender; wait for the playlist to be finalized
            await senderTask;
            (sender as IDisposable)?.Dispose();
            logger.ZLogInformation($"RTMP connection closed: {receiver.ToString()}");
        }
    }
}
