using System.Net;
using System.Net.Sockets;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Net.Transport.SRT;
using Spangle.SRT;
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

        listener.Start();
        _logger.ZLogInformation("Starting to accept connections.");

        while (true)
        {
            try
            {
                var srtClient = await listener.AcceptSRTClientAsync();
                _ = Task.Run(() => ProcessConnection(receiver, srtClient, _logger));
            }
            catch (Exception e)
            {
                _logger.ZLogError("Error: {0}", e);
            }
        }
    }

    private static async ValueTask ProcessConnection(SRTReceiver receiver, SRTClient srtClient, ILogger logger)
    {
        string id = ZString.Concat(srtClient.GetHashCode());
        try
        {
            logger.ZLogDebug("Connection opened: {0}", id);
            await receiver.BeginReadAsync(id, srtClient.Pipe);
        }
        catch (Exception e)
        {
            logger.ZLogError("Error: {0}", e);
        }
        finally
        {
            srtClient.Dispose();
            logger.ZLogDebug("Connection closed: {0}", id);
        }
    }
}
