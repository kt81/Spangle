using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Spangle.Rtmp;

Trace.Listeners.Add(new ConsoleTraceListener());
Trace.AutoFlush = true;

var listenEndPoint = new IPEndPoint(IPAddress.Any, 1935);
var listener = new TcpListener(listenEndPoint); 
listener.Start();

while (true)
{
    try
    {
        // 単純なコネクション準備。Kestrelとかにうつすこと
        // このBufferedStreamは接続中のpeerとずっと使いまわす
        var tcpClient = await listener.AcceptTcpClientAsync();
        await using var stream = new BufferedStream(tcpClient.GetStream());

        var rtmp = new RtmpReceiver(stream);
        await rtmp.BeginReadAsync();
    }
    catch (Exception e)
    {
        Debug.WriteLine(e.ToString());
    }
}
