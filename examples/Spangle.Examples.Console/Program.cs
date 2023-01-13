using System.Net;
using System.Net.Sockets;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Examples.Console;
using Spangle.Logging;
using Spangle.Rtmp;
using ZLogger;

var loggerFactory = LoggerFactory.Create(conf =>
{
    conf.AddFilter("Spangle", LogLevel.Trace)
        .AddColorizedZLoggerConsole("Spangle");
});
SpangleLogManager.SetLoggerFactory(loggerFactory);
var logger = loggerFactory.CreateLogger("Spangle.Examples.Console");

var listenEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 1935);
var listener = new TcpListener(listenEndPoint);
RtmpReceiver receiver = new RtmpReceiver();

listener.Start();
logger.ZLogInformation("Starting to accept connection.");

// Do NOT use this code in production!
while (true)
{
    try
    {
        var tcpClient = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => ProcessConnection(receiver, tcpClient, logger));
    }
    catch (Exception e)
    {
        logger.ZLogError("Error: {0}", e);
    }
}

static async ValueTask ProcessConnection(RtmpReceiver receiver, TcpClient tcpClient, ILogger logger)
{
    string id = ZString.Concat(tcpClient.GetHashCode(), "[", tcpClient.Client.RemoteEndPoint?.ToString() ?? "none", "]");
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
