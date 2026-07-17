using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtsp;
using ZLogger;

namespace Spangle.Extensions.Kestrel;

/// <summary>
/// Drives every configured RTSP pull source: connects out, republishes the stream
/// as HLS/CMAF, and reconnects with exponential backoff when the source drops —
/// cameras reboot, networks blip, and a pull ingest is expected to just recover.
/// </summary>
public sealed class RtspIngestService(
    IOptions<SpangleMediaServerOptions> options,
    HLSStreamRegistry registry,
    PublishSessionRegistry publishSessions,
    IPublishAuthorizer publishAuthorizer,
    IHLSStorage storage,
    RtspDialectRegistry dialects,
    IEnumerable<IPublishEgressFactory> egressFactories,
    ILogger<RtspIngestService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RtspOptions rtsp = options.Value.Rtsp;
        if (!rtsp.Enabled || rtsp.Sources.Count == 0)
        {
            return Task.CompletedTask;
        }

        // one independent connect-reconnect loop per source
        IEnumerable<Task> loops = rtsp.Sources.Select(source => RunSourceAsync(source, stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task RunSourceAsync(RtspSourceOptions source, CancellationToken ct)
    {
        RtspDialect dialect = dialects.Resolve(source.Dialect, out bool known);
        if (!known)
        {
            logger.ZLogWarning($"Unknown RTSP dialect `{source.Dialect}` for `{source.Name}`; using the default");
        }

        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(source.ReconnectMaxDelaySeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(source, dialect, ct).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1); // a clean end resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.ZLogWarning($"RTSP source `{source.Name}` error: {e.Message}");
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }
    }

    private async Task ConnectAndStreamAsync(RtspSourceOptions source, RtspDialect dialect, CancellationToken ct)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            throw new RtspProtocolException($"`{source.Url}` is not a valid rtsp:// URL");
        }
        (string? user, string? pass) = ResolveCredentials(source, uri);
        int port = uri.Port > 0 ? uri.Port : 554;

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(uri.Host, port, ct).ConfigureAwait(false);
        var endPoint = socket.RemoteEndPoint ?? new DnsEndPoint(uri.Host, port);
        using var stream = new NetworkStream(socket, ownsSocket: false);

        PipeReader reader = PipeReader.Create(stream);
        PipeWriter writer = PipeWriter.Create(stream);

        var receiver = new RtspReceiverContext(reader, writer, source.Url, source.Name, endPoint,
            user, pass, dialect, ct, source.Transport);
        await StreamToHlsAsync(receiver, ct).ConfigureAwait(false);
    }

    private async Task StreamToHlsAsync(RtspReceiverContext receiver, CancellationToken ct)
    {
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
        // Additional egresses (e.g. MOQT) ride the same MediaFrame stream through a fan-out
        var egresses = SessionEgresses.Start(egressFactories, ct);
        using var live = new LiveContext(receiver, hls,
            publishSessions: publishSessions, publishAuthorizer: publishAuthorizer,
            additionalSenders: egresses.Senders, cancellationToken: ct);
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
            logger.ZLogInformation($"RTSP source connecting: {receiver.ToString()}");
            await live.StartAsync().ConfigureAwait(false);
        }
        finally
        {
            await senderTask.ConfigureAwait(false);
            (sender as IDisposable)?.Dispose();
            await egresses.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static (string? User, string? Password) ResolveCredentials(RtspSourceOptions source, Uri uri)
    {
        // config wins; otherwise take credentials embedded in the URL's userinfo
        if (!string.IsNullOrEmpty(source.UserName))
        {
            return (source.UserName, source.Password);
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);
        }
        return (null, null);
    }
}
