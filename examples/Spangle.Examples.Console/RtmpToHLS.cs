using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Spangle.Transport.Rtmp;
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
        var listener = new TcpListener(listenEndPoint);
        using RtmpReceiver receiver = new RtmpReceiver();
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
                _ = Task.Run(() => ProcessConnection(receiver, tcpClient, _logger, ct), ct);
            }
            catch (Exception e)
            {
                _logger.ZLogError($"Error: {e}");
            }
        }
    }

    private static async Task ProcessConnection(RtmpReceiver receiver, TcpClient tcpClient, ILogger logger, CancellationToken ct)
    {
        var context = RtmpReceiverContext.CreateFromTcpClient(tcpClient, ct);
        try
        {
            logger.ZLogDebug($"Connection opened: {context.ToString()}");
            await receiver.StartAsync(context);
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error: {e}");
        }
        finally
        {
            tcpClient.Dispose();
            logger.ZLogDebug($"Connection closed: {context.ToString()}");
        }
    }
}
