using System.Net;
using System.Net.Sockets;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Protocols.Rtmp;
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

        listener.Start();
        _logger.ZLogInformation("Starting to accept connections.");

        while (true)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => ProcessConnection(receiver, tcpClient, _logger));
            }
            catch (Exception e)
            {
                _logger.ZLogError("Error: {0}", e);
            }
        }
    }

    private static async ValueTask ProcessConnection(RtmpReceiver receiver, TcpClient tcpClient, ILogger logger)
    {
        string id = ZString.Concat(tcpClient.GetHashCode(), "[",
            tcpClient.Client.RemoteEndPoint?.ToString() ?? "none", "]");
        try
        {
            logger.ZLogDebug("Connection opened: {0}", id);
            await receiver.BeginReadAsync(id, tcpClient.GetStream());
        }
        catch (Exception e)
        {
            logger.ZLogError("Error: {0}", e);
        }
        finally
        {
            tcpClient.Dispose();
            logger.ZLogDebug("Connection closed: {0}", id);
        }
    }
}
