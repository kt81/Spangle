using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Spangle.Spinner;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtmp;
using ValueTaskSupplement;
using ZLogger;

namespace Spangle.Examples.Console;

public class RtmpToHLS
{
    private readonly ILogger<RtmpToHLS> _logger;

    public RtmpToHLS(ILogger<RtmpToHLS> logger)
    {
        _logger = logger;
    }

    // Do NOT use this code in production!
    public async ValueTask Start()
    {
        var listenEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 1935);
        using var listener = new TcpListener(listenEndPoint);

        // TODO to be able to specify the container format (TS or fMP4(CMAF))
        using HLSSender sender = new HLSSender();

        var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, _) =>
        {
            cts.Cancel();
        };
        var ct = cts.Token;

        listener.Start();
        _logger.ZLogInformation($"Starting to accept connections.");

        while (true)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ProcessConnection(sender, tcpClient, _logger, ct), ct);
            }
            catch (Exception e)
            {
                _logger.ZLogError($"Error: {e}");
            }
        }
    }

    private static async Task ProcessConnection(HLSSender sender, TcpClient tcpClient,
        ILogger logger, CancellationToken ct)
    {
        var rtmp = RtmpReceiverContext.CreateFromTcpClient(tcpClient, ct);
        var hls = new HLSSenderContext(ct) { OutputDirectory = "hls-out" };
        LiveContext live = new LiveContext(rtmp, hls);
        var senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.StartAsync(hls);
            }
            catch (Exception e)
            {
                logger.ZLogError($"Sender error: {e}");
            }
        }, ct);
        try
        {
            logger.ZLogDebug($"Connection opened: {rtmp.ToString()}");
            await live.StartAsync();
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error: {e}");
        }
        finally
        {
            tcpClient.Dispose();
            // The completion propagates receiver -> spinner -> sender; wait for the playlist to be finalized
            await senderTask;
            logger.ZLogDebug($"Connection closed: {rtmp.ToString()}");
        }
    }
}
