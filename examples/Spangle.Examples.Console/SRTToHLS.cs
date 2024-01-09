using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Net.Transport.SRT;
using Spangle.Transport.SRT;
using ZLogger;

namespace Spangle.Examples.Console;

public class SRTToHLS
{
    private readonly ILogger<SRTToHLS> _logger;

    public SRTToHLS(ILogger<SRTToHLS> logger)
    {
        _logger = logger;
    }

    // Do NOT use this code in production!
    public async ValueTask Start()
    {
        var listenEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 2000);
        var listener = new SRTListener(listenEndPoint);
        using SRTReceiver receiver = new SRTReceiver();
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
            if (cts.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var srtClient = await listener.AcceptSRTClientAsync(ct);
                // ReSharper disable once AccessToDisposedClosure
                _ = Task.Run(() => ProcessConnection(receiver, srtClient, _logger, ct), ct);
            }
            catch (Exception e)
            {
                _logger.ZLogError($"Error: {e}");
            }
        }
    }

    private static async Task ProcessConnection(SRTReceiver receiver, SRTClient srtClient, ILogger logger, CancellationToken ct)
    {
        var context = new SRTReceiverContext(srtClient, ct);
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
            srtClient.Dispose();
            logger.ZLogDebug($"Connection closed: {context.ToString()}");
        }
    }
}
