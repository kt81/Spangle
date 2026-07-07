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
        var hls = new HLSSenderContext(ct)
        {
            OutputDirectory = hlsOptions.OutputDirectory,
            TargetSegmentDuration = hlsOptions.TargetSegmentDuration,
        };
        using var live = new LiveContext(receiver, hls, ct);
        using var sender = new HLSSender();

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
            logger.ZLogInformation($"RTMP connection closed: {receiver.ToString()}");
        }
    }
}
