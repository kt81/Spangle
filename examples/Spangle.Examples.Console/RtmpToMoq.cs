using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Spangle.Extensions.Moqt;
using Spangle.Transport.Rtmp;
using ZLogger;

namespace Spangle.Examples.Console;

/// <summary>
/// RTMP in, MOQT out: each published stream is announced to a relay, its frames go out as LOC
/// objects, and an MSF catalog beside them tells a player what is there. A browser needs only the
/// relay's address and the namespace.
/// <para>
/// The relay is given as <c>SPANGLE_MOQ_RELAY</c> (host:port) and the namespace as
/// <c>SPANGLE_MOQ_NAMESPACE</c>. The relay's certificate is not validated — this is an example
/// pointed at a development relay.
/// </para>
/// </summary>
internal sealed class RtmpToMoq
{
    private readonly ILogger<RtmpToMoq> _logger;

    public RtmpToMoq(ILogger<RtmpToMoq> logger) => _logger = logger;

    // Do NOT use this code in production!
    public async ValueTask StartAsync()
    {
        string relay = Environment.GetEnvironmentVariable("SPANGLE_MOQ_RELAY") ?? "127.0.0.1:4433";
        string @namespace = Environment.GetEnvironmentVariable("SPANGLE_MOQ_NAMESPACE") ?? "vc";
        string[] parts = relay.Split(':');
        var options = new MoqSenderOptions
        {
            Relay = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture)),
            Namespace = @namespace,
            TargetHost = "localhost",
            AllowUntrustedRelayCertificate = true,
        };

        var listenEndPoint = new IPEndPoint(IPAddress.Any, 1935);
        using var listener = new TcpListener(listenEndPoint);

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        CancellationToken ct = cts.Token;

        listener.Start();
        _logger.ZLogInformation($"RTMP on {listenEndPoint}; publishing to MOQT relay {relay} as '{@namespace}'.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync(ct);
                // ownership of the client transfers to the connection task
#pragma warning disable CA2025
                _ = Task.Run(() => ProcessConnectionAsync(tcpClient, options, _logger, ct), ct);
#pragma warning restore CA2025
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

    private static async Task ProcessConnectionAsync(TcpClient tcpClient, MoqSenderOptions options, ILogger logger,
        CancellationToken ct)
    {
        RtmpReceiverContext rtmp = RtmpReceiverContext.CreateFromTcpClient(tcpClient, ct);
        var moq = new MoqSenderContext(options, ct);
        using var live = new LiveContext(rtmp, moq, cancellationToken: ct);
        var sender = new MoqSender();

        // The sender blocks on its intake long before the receiver has written anything, so it runs
        // beside the session and is awaited once the session's completion has propagated to it.
        Task senderTask = Task.Run(async () =>
        {
            try
            {
                await sender.StartAsync(moq);
            }
            catch (Exception e)
            {
                logger.ZLogError($"MOQT sender error: {e}");
            }
        }, CancellationToken.None);

        try
        {
            logger.ZLogInformation($"Connection opened: {rtmp.ToString()}");
            await live.StartAsync();
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error: {e}");
        }
        finally
        {
            tcpClient.Dispose();
            await senderTask;
            await sender.DisposeAsync();
            logger.ZLogInformation($"Connection closed: {rtmp.ToString()}");
        }
    }
}
