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

        using RtmpReceiver receiver = new RtmpReceiver();
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
                _ = Task.Run(() => ProcessConnection(receiver, sender, tcpClient, _logger, ct), ct);
            }
            catch (Exception e)
            {
                _logger.ZLogError($"Error: {e}");
            }
        }
    }

    private static async Task ProcessConnection(RtmpReceiver receiver, HLSSender sender, TcpClient tcpClient,
        ILogger logger, CancellationToken ct)
    {
        var rtmp = RtmpReceiverContext.CreateFromTcpClient(tcpClient, ct);
        var hls = new HLSSenderContext(ct);
        var spinner = new NALFileToAnnexBSpinner(rtmp, hls, ct);
        try
        {
            logger.ZLogDebug($"Connection opened: {rtmp.ToString()}");
            await ValueTaskEx.WhenAll(
                receiver.StartAsync(rtmp),
                sender.StartAsync(hls),
                spinner.SpinAsync());
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error: {e}");
        }
        finally
        {
            tcpClient.Dispose();
            logger.ZLogDebug($"Connection closed: {rtmp.ToString()}");
        }
    }
}
