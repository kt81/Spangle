using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Net.Transport.SRT;
using Spangle.Transport.HLS;
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
        var registry = new HLSStreamRegistry();
        var sessions = new PublishSessionRegistry();
        var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, _) =>
        {
            cts.Cancel();
        };
        var ct = cts.Token;

        listener.Start();
        _logger.ZLogInformation($"Starting to accept SRT connections on {listenEndPoint}");
        _logger.ZLogInformation($"Try: ffmpeg -re -f lavfi -i testsrc2 -c:v libx264 -g 30 -f mpegts \"srt://127.0.0.1:2000?streamid=live/test\"");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var srtClient = await listener.AcceptSRTClientAsync(ct);
                _ = Task.Run(() => ProcessConnection(srtClient, registry, sessions, _logger, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.ZLogError($"Error: {e}");
            }
        }
    }

    private static async Task ProcessConnection(SRTClient srtClient, HLSStreamRegistry registry,
        PublishSessionRegistry sessions, ILogger logger, CancellationToken ct)
    {
        var receiver = new SRTReceiverContext(srtClient, ct);
        var hls = new HLSSenderContext(ct)
        {
            OutputDirectory = "hls-out",
            Registry = registry,
        };
        using var live = new LiveContext(receiver, hls, ct, publishSessions: sessions);
        var sender = new HLSSender();

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
        catch (Exception e)
        {
            logger.ZLogError($"Error: {e}");
        }
        finally
        {
            await senderTask;
            srtClient.Dispose();
            logger.ZLogInformation($"SRT connection closed: {receiver.ToString()}");
        }
    }
}
