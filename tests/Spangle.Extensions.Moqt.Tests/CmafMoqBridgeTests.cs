using System.Net;
using System.Net.Security;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.InMemory;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// The Spangle → MOQT egress bridge end to end, in process: an init segment plus two media
/// segments are published through <see cref="CmafMoqTrackBridge"/> onto a <see cref="MoqPublisher"/>,
/// and a native <see cref="MoqSubscriber"/> reconstructs the exact bytes group by group. The bridge
/// carries opaque fragment bytes verbatim, so fragment-shaped payloads stand in for real fMP4 here
/// — whether a real player decodes the fMP4 is an interop (M3) concern, not the bridge's contract.
/// </summary>
public class CmafMoqBridgeTests
{
    private static readonly SslApplicationProtocol Alpn = new(MoqtConstants.Alpn);

    [Fact]
    public async Task Bridge_PublishesInitAndSegments_SubscriberReconstructsThemInOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken ct = cts.Token;

        // Distinct, non-trivially sized payloads standing in for the init and two media segments.
        byte[] init = Pattern(0x11, 300);
        byte[] segment0 = Pattern(0x22, 1500);
        byte[] segment1 = Pattern(0x33, 1800);

        var transport = new InMemoryQuicTransport();
        await using IQuicServer server = await transport.ListenAsync(new QuicServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [Alpn],
        }, ct);

        ValueTask<IQuicConnection> acceptConn = server.AcceptConnectionAsync(ct);
        await using IQuicConnection clientConn = await transport.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = server.LocalEndPoint,
            ApplicationProtocols = [Alpn],
        }, ct);
        await using IQuicConnection serverConn = await acceptConn;

        Task<MoqSession> pubSessionTask = MoqSession.AcceptAsync(serverConn, new SetupMessage(), ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(clientConn, new SetupMessage(), ct);
        await using MoqSession pubSession = await pubSessionTask;

        FullTrackName track = FullTrackName.FromStrings(["live", "demo"], "video0");

        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        var bridge = new CmafMoqTrackBridge(publisher.PublishTrack(track));
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        Task<IReadOnlyList<(ulong Group, byte[] Payload)>> subscriberSide =
            CollectAsync(subscriber, track, expected: 3, ct);

        await bridge.PublishInitAsync(init, ct);
        await bridge.PublishSegmentAsync(segment0, ct);
        await bridge.PublishSegmentAsync(segment1, ct);

        IReadOnlyList<(ulong Group, byte[] Payload)> received = await subscriberSide;

        received.Select(r => r.Group).Should().Equal([0UL, 1UL, 2UL], "init then each media segment is its own group");
        received[0].Payload.Should().Equal(init, "the init segment round-trips byte for byte");
        received[1].Payload.Should().Equal(segment0);
        received[2].Payload.Should().Equal(segment1);

        await runCts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // the demux loop is cancelled once the flow is verified
        }
    }

    [Fact]
    public async Task Bridge_RejectsSegmentBeforeInit()
    {
        var transport = new InMemoryQuicTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IQuicServer server = await transport.ListenAsync(new QuicServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [Alpn],
        }, cts.Token);

        ValueTask<IQuicConnection> acceptConn = server.AcceptConnectionAsync(cts.Token);
        await using IQuicConnection clientConn = await transport.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = server.LocalEndPoint,
            ApplicationProtocols = [Alpn],
        }, cts.Token);
        await using IQuicConnection serverConn = await acceptConn;

        Task<MoqSession> pubSessionTask = MoqSession.AcceptAsync(serverConn, new SetupMessage(), cts.Token);
        await using MoqSession subSession = await MoqSession.ConnectAsync(clientConn, new SetupMessage(), cts.Token);
        await using MoqSession pubSession = await pubSessionTask;

        var bridge = new CmafMoqTrackBridge(
            MoqPublisher.Create(pubSession).PublishTrack(FullTrackName.FromStrings(["live"], "video0")));

        Func<Task> act = async () => await bridge.PublishSegmentAsync(Pattern(0x44, 10), cts.Token);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static byte[] Pattern(byte seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }

    private static async Task<IReadOnlyList<(ulong Group, byte[] Payload)>> CollectAsync(
        MoqSubscriber subscriber, FullTrackName track, int expected, CancellationToken ct)
    {
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);
        var received = new List<(ulong, byte[])>();
        await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct))
        {
            received.Add((moqObject.GroupId, moqObject.Payload.ToArray()));
            if (received.Count == expected)
            {
                break;
            }
        }

        return received;
    }
}
