using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Spangle.Examples.Console;
using Spangle.Rtmp;
using ZLogger;

var listenEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 1935);
var listener = new TcpListener(listenEndPoint);
var loggerFactory = LoggerFactory.Create(conf =>
{
    conf.AddFilter("Spangle", LogLevel.Trace)
        .AddColorizedZLoggerConsole("Spangle");
});
var logger = loggerFactory.CreateLogger("Spangle.Examples.Console");

listener.Start();
logger.ZLogInformation("Starting to accept connection.");

// Do NOT use this code in production!
while (true)
{
    try
    {
        var tcpClient = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => ProcessConnection(tcpClient, logger, loggerFactory) );
    }
    catch (Exception e)
    {
        logger.ZLogError("Error: {0}", e);
    }
}

static async ValueTask ProcessConnection(TcpClient tcpClient, ILogger logger, ILoggerFactory loggerFactory)
{
    try
    {
        logger.ZLogInformation("Connection opened [{0}]", tcpClient.GetHashCode());
        await using var stream = tcpClient.GetStream();
        var rtmp = new RtmpReceiver(stream, loggerFactory);
        await rtmp.BeginReadAsync();
    }
    catch (Exception e)
    {
        logger.ZLogError("Error: {0}", e);
    }
    finally
    {
        logger.ZLogInformation("Connection closed [{0}]", tcpClient.GetHashCode());
    }
}
