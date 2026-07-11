using System.Net;
using Microsoft.Extensions.Options;
using Spangle.Net.Transport.SRT;
using Spangle.Transport.HLS;
using Spangle.Transport.SRT;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Hosts the SRT ingest endpoint: accepts SRT publishers (routed by their streamid)
/// and writes HLS output, exactly like <see cref="RtmpConnectionHandler"/> does for RTMP.
/// SRT is UDP-based and brings its own listener, so this runs beside Kestrel as a
/// hosted service instead of a connection handler.
/// </summary>
public sealed class SrtIngestService(
    IOptions<SpangleMediaServerOptions> options,
    HLSStreamRegistry registry,
    PublishSessionRegistry publishSessions,
    IPublishAuthorizer publishAuthorizer,
    IHLSStorage storage,
    Spangle.Spinner.TimedMetadataHub metadataHub,
    ILogger<SrtIngestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SrtOptions srtOptions = options.Value.Srt;
        if (!srtOptions.Enabled)
        {
            return;
        }

        using var listener = new SRTListener(
            new IPEndPoint(IPAddress.Any, srtOptions.Port),
            srtOptions.Passphrase is null ? null : new SRTListenerOptions { Passphrase = srtOptions.Passphrase },
            logger);
        listener.Start();
        logger.ZLogInformation($"SRT ingest listening on port {srtOptions.Port} (encryption: {(srtOptions.Passphrase is null ? "off" : "passphrase")})");

        while (!stoppingToken.IsCancellationRequested)
        {
            SRTClient client;
            try
            {
                client = await listener.AcceptSRTClientAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                logger.ZLogError($"SRT accept failed: {e}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, stoppingToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(SRTClient client, CancellationToken ct)
    {
        HlsOptions hlsOptions = options.Value.Hls;
        var segmentFormat = hlsOptions.SegmentFormat.ToLowerInvariant() switch
        {
            "fmp4" or "cmaf" or "mp4" => HLSSegmentFormat.Fmp4,
            _ => HLSSegmentFormat.MpegTs,
        };
        // TS in, TS out: re-segment the source packets instead of demux+remux
        bool passthrough = segmentFormat == HLSSegmentFormat.MpegTs && hlsOptions.TsPassthrough;
        var receiver = new SRTReceiverContext(client, ct) { RawTsPassthrough = passthrough };
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
        // Metadata injection needs the MediaFrame pipeline; the raw TS passthrough
        // has none (a documented trade-off of that path)
        IReadOnlyList<Spangle.Spinner.ISpinner>? spinners =
            !passthrough && options.Value.Http.MetadataInjection
                ? [new Spangle.Spinner.TimedMetadataInjector(receiver, metadataHub, ct)]
                : null;
        using var live = new LiveContext(receiver, hls, ct, mediaSpinners: spinners,
            publishSessions: publishSessions, publishAuthorizer: publishAuthorizer);
        ISender<HLSSenderContext> sender = segmentFormat == HLSSegmentFormat.Fmp4
            ? new CmafHLSSender()
            : passthrough
                ? new TSPassthroughHLSSender()
                : new HLSSender();
        if (passthrough)
        {
            // No codec event fires in raw mode; the receiver feeds the sender directly
            receiver.MediaOutlet = hls.Intake;
        }

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
            logger.ZLogInformation($"SRT connection opened: {receiver.ToString()}");
            await live.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // normal at sender disconnect or server shutdown
        }
        catch (Exception e)
        {
            logger.ZLogError($"SRT receiver error: {e}");
        }
        finally
        {
            // completion propagates receiver -> spinner -> sender; wait for the playlist to finalize
            await senderTask;
            (sender as IDisposable)?.Dispose();
            client.Dispose();
            logger.ZLogInformation($"SRT connection closed: {receiver.ToString()}");
        }
    }
}
