using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtsp.Server;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Handles one RTSP push connection on the listen port: a client (e.g. ffmpeg)
/// ANNOUNCE/SETUP/RECORDs a stream, and it is republished as HLS/CMAF exactly like an
/// RTMP or SRT publisher. RTSP push is a publish, so it goes through the same
/// <see cref="IPublishAuthorizer"/> and takeover path.
/// </summary>
public sealed class RtspConnectionHandler(
    IOptions<SpangleMediaServerOptions> options,
    IHostApplicationLifetime lifetime,
    HLSStreamRegistry registry,
    PublishSessionRegistry publishSessions,
    IPublishAuthorizer publishAuthorizer,
    IHLSStorage storage,
    ILogger<RtspConnectionHandler> logger) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            connection.ConnectionClosed, lifetime.ApplicationStopping);
        var ct = cts.Token;

        var receiver = new RtspPushReceiverContext(
            connection.Transport.Input, connection.Transport.Output,
            connection.RemoteEndPoint!, ct);

        HlsOptions hlsOptions = options.Value.Hls;
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
        using var live = new Spangle.LiveContext(receiver, hls,
            publishSessions: publishSessions, publishAuthorizer: publishAuthorizer, cancellationToken: ct);
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
            logger.ZLogInformation($"RTSP push connection opened: {receiver.ToString()}");
            await live.StartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal at client disconnect or server shutdown
        }
        catch (ConnectionResetException)
        {
            logger.ZLogInformation($"RTSP publisher disconnected abruptly: {receiver.ToString()}");
        }
        catch (Exception e)
        {
            logger.ZLogError($"RTSP push receiver error: {e}");
        }
        finally
        {
            await senderTask.ConfigureAwait(false);
            (sender as IDisposable)?.Dispose();
            logger.ZLogInformation($"RTSP push connection closed: {receiver.ToString()}");
        }
    }
}
