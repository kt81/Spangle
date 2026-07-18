using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using Spangle.Net.Moqt;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.InMemory;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// Regression tests for the connection state machine of <see cref="MoqSender"/> and
/// <see cref="MoqRelayConnection"/>, over the in-memory transport with a scripted
/// <see cref="InProcessMoqRelay"/> as the peer. Each test pins one behavior the commit history
/// claims and the code comments explain: a rejected announce fails the dial without wedging the
/// egress, a connection that dies mid-SETUP is released before the throw, configs captured while
/// no relay is reachable still make the catalog, and a dead relay is torn down, redialed, and
/// resumed with group ids above everything the dead connection published.
/// </summary>
public class MoqRelayConnectionTests
{
    private static readonly byte[] AvcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
    private static readonly byte[] AacAsc = [0x12, 0x10]; // AAC-LC, 44.1 kHz, stereo
    private static readonly byte[] KeyframePayload = Pattern(0x33, 64);
    private static readonly string[] NamespaceFields = ["live", "test"];
    private static readonly FullTrackName VideoTrackName = FullTrackName.FromStrings(NamespaceFields, "video0");

    [Fact]
    public async Task ARejectedAnnounce_FailsTheDial_AndTheNextFrameRedials()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        var transport = new InMemoryQuicTransport();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 46001);
        await using InProcessMoqRelay relay = await InProcessMoqRelay.StartAsync(transport, endpoint, ct);
        relay.FailNextAnnounce();

        var context = new MoqSenderContext(Options(endpoint), ct) { Transport = transport };
        await using var sender = new MoqSender();
        Task senderTask = sender.StartAsync(context).AsTask();

        await WriteConfigAsync(context, MediaFrameKind.Video, (uint)VideoCodec.H264, AvcC);
        await WriteKeyframeAsync(context);

        // Dial #1: the announce is answered REQUEST_ERROR, so ConnectAsync must fail...
        await using RelaySession first = await relay.AcceptSessionAsync(ct);
        (await first.WaitForAnnounceAsync(ct)).Should().BeFalse("the first announce is scripted to fail");

        // ...and the next frame must dial again — the regression here was a sender wedged forever.
        await WriteKeyframeAsync(context);
        await using RelaySession second = await relay.AcceptSessionAsync(ct);
        (await second.WaitForAnnounceAsync(ct)).Should().BeTrue("nothing is scripted to fail the second announce");

        // The recovered connection is not just announced but publishing: subscribe like a relay
        // with a viewer and receive a frame.
        ulong videoAlias = await second.SubscribeAsync(VideoTrackName, ct);
        MoqObject published = await PumpKeyframesUntilObjectAsync(context, second, videoAlias, ct);
        published.Payload.ToArray().Should().Equal(KeyframePayload, "the egress resumed publishing after the failed dial");

        senderTask.IsCompleted.Should().BeFalse("a failed announce must not kill the egress loop");
        await context.Intake.CompleteAsync();
        await senderTask;
    }

    [Fact]
    public async Task ARelayDyingDuringSetup_MakesConnectAsyncReleaseTheConnectionAndThrow()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        var transport = new InMemoryQuicTransport();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 46002);
        await using IQuicServer server = await transport.ListenAsync(new QuicServerOptions
        {
            ListenEndPoint = endpoint,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
        }, ct);

        // The "relay": accept the QUIC connection, then drop it before the SETUP is answered.
        Task slam = Task.Run(async () =>
        {
            IQuicConnection accepted = await server.AcceptConnectionAsync(ct);
            await accepted.CloseAsync(0, ct);
            await accepted.DisposeAsync();
        }, CancellationToken.None);

        var tracking = new TrackingQuicTransport(transport);
        var context = new MoqSenderContext(Options(endpoint), ct) { Transport = tracking };

        Func<Task> dial = () => MoqRelayConnection.ConnectAsync(context, ct);
        await dial.Should().ThrowAsync<Exception>("the relay died before the SETUP completed");
        await slam;

        // The regression here was one leaked QUIC connection per failed dial: a connection that
        // never became a MoqRelayConnection must be released by ConnectAsync itself.
        tracking.Connections.Should().ContainSingle("one dial opens one connection")
            .Which.IsDisposed.Should().BeTrue("ConnectAsync must release the connection it dialed");
    }

    [Fact]
    public async Task ConfigsArrivingWhileNoRelayIsReachable_StillMakeTheFirstCatalog()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        var transport = new InMemoryQuicTransport();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 46003);

        // No relay is listening yet: the stream declares itself while the relay is down.
        var context = new MoqSenderContext(Options(endpoint), ct) { Transport = transport };
        await using var sender = new MoqSender();
        Task senderTask = sender.StartAsync(context).AsTask();

        await WriteConfigAsync(context, MediaFrameKind.Video, (uint)VideoCodec.H264, AvcC);
        await WriteConfigAsync(context, MediaFrameKind.Audio, (uint)AudioCodec.AAC, AacAsc);
        await WriteKeyframeAsync(context); // triggers a dial, which is refused — nothing listens

        await using InProcessMoqRelay relay = await InProcessMoqRelay.StartAsync(transport, endpoint, ct);
        await WriteKeyframeAsync(context); // the next frame redials, and now the relay is there

        await using RelaySession session = await relay.AcceptSessionAsync(ct);
        (await session.WaitForAnnounceAsync(ct)).Should().BeTrue();

        ulong catalogAlias = await session.SubscribeAsync(
            MoqCatalogTrack.NameIn(TrackNamespace.FromStrings(NamespaceFields)), ct);
        MoqObject catalogObject = await NextObjectAsync(session, catalogAlias, ct);

        // The configs arrived before any connection existed; the first catalog must list both
        // tracks anyway — they are session state, captured before the connection gate.
        MsfCatalog catalog = MsfCatalog.Parse(catalogObject.Payload.Span);
        catalog.Tracks.Select(t => t.Name).Should().BeEquivalentTo(["video0", "audio0"],
            "both configs were captured while the relay was down");
        catalog.Tracks.Single(t => t.Name == "video0").Codec.Should().Be("avc1.64001F");
        catalog.Tracks.Single(t => t.Name == "audio0").Codec.Should().Be("mp4a.40.2");

        await context.Intake.CompleteAsync();
        await senderTask;
    }

    [Fact]
    public async Task ADeadRelay_IsTornDownAndRedialed_AndGroupsResumeAboveTheDeadConnections()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        var transport = new InMemoryQuicTransport();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 46004);
        await using InProcessMoqRelay relay = await InProcessMoqRelay.StartAsync(transport, endpoint, ct);

        var context = new MoqSenderContext(Options(endpoint), ct) { Transport = transport };
        await using var sender = new MoqSender();
        Task senderTask = sender.StartAsync(context).AsTask();

        await WriteConfigAsync(context, MediaFrameKind.Video, (uint)VideoCodec.H264, AvcC);
        await WriteKeyframeAsync(context);

        await using RelaySession first = await relay.AcceptSessionAsync(ct);
        (await first.WaitForAnnounceAsync(ct)).Should().BeTrue();
        ulong firstAlias = await first.SubscribeAsync(VideoTrackName, ct);
        MoqObject beforeDeath = await PumpKeyframesUntilObjectAsync(context, first, firstAlias, ct);

        // The relay dies under the publisher. Give the wall clock a moment so the redial's
        // millisecond-derived first group id lands above every group the dead connection used.
        await first.KillAsync();
        await Task.Delay(50, ct);

        // Keep the live source live: each frame is the sender's chance to notice the corpse, tear
        // it down, and redial (ReconnectDelay is zero, so the same frame that finds the corpse
        // also dials).
        Task<RelaySession> redialed = relay.AcceptSessionAsync(ct);
        while (!redialed.IsCompleted)
        {
            await WriteKeyframeAsync(context);
            await Task.Delay(50, ct);
        }

        await using RelaySession second = await redialed;
        (await second.WaitForAnnounceAsync(ct)).Should().BeTrue("the redial announces the namespace again");

        ulong secondAlias = await second.SubscribeAsync(VideoTrackName, ct);
        MoqObject afterRedial = await PumpKeyframesUntilObjectAsync(context, second, secondAlias, ct);

        afterRedial.Payload.ToArray().Should().Equal(KeyframePayload,
            "the captured codec config survives the reconnect, so frames publish again");
        afterRedial.GroupId.Should().BeGreaterThan(beforeDeath.GroupId,
            "a redialed connection must number its groups above everything the dead one published — a relay caches by group id, and a reused id drops the subscriber");

        await context.Intake.CompleteAsync();
        await senderTask;
    }

    /// <summary>
    /// Options for a sender under test: a fixed namespace (no stream key involved), no reconnect
    /// backoff — the reconnect gate itself is what these tests drive, one dial per frame — and a
    /// fast catalog timer.
    /// </summary>
    private static MoqSenderOptions Options(IPEndPoint relay) => new()
    {
        Relay = relay,
        Namespace = string.Join('/', NamespaceFields),
        ReconnectDelay = TimeSpan.Zero,
        CatalogInterval = TimeSpan.FromMilliseconds(50),
    };

    private static async Task WriteFrameAsync(PipeWriter intake, MediaFrameKind kind, MediaFrameFlags flags,
        uint codec, byte[] payload)
    {
        MediaFrameHeader.Write(intake, kind, flags, codec, compositionTimeMs: 0, payload.Length, timestamp: 0);
        intake.Write(payload);
        await intake.FlushAsync();
    }

    private static Task WriteConfigAsync(MoqSenderContext context, MediaFrameKind kind, uint codec, byte[] config) =>
        WriteFrameAsync(context.Intake, kind, MediaFrameFlags.Config, codec, config);

    private static Task WriteKeyframeAsync(MoqSenderContext context) =>
        WriteFrameAsync(context.Intake, MediaFrameKind.Video, MediaFrameFlags.KeyFrame, (uint)VideoCodec.H264,
            KeyframePayload);

    /// <summary>The next media object (a real payload, not a group-end marker) on <paramref name="alias"/>.</summary>
    private static async Task<MoqObject> NextObjectAsync(RelaySession session, ulong alias, CancellationToken ct)
    {
        while (true)
        {
            (ulong a, MoqObject moqObject) = await session.Objects.ReadAsync(ct);
            if (a == alias && moqObject.Payload.Length > 0)
            {
                return moqObject;
            }
        }
    }

    /// <summary>
    /// Feeds keyframes to the sender until one comes out at the relay on <paramref name="alias"/>.
    /// A live source cannot wait — frames keep coming whatever the connection state — and the
    /// first frames are legitimately dropped (no subscriber yet, or a teardown in progress), so
    /// the test keeps producing like a source would until the pipeline is demonstrably flowing.
    /// </summary>
    private static async Task<MoqObject> PumpKeyframesUntilObjectAsync(MoqSenderContext context,
        RelaySession session, ulong alias, CancellationToken ct)
    {
        Task<MoqObject> firstObject = NextObjectAsync(session, alias, ct);
        while (!firstObject.IsCompleted)
        {
            await WriteKeyframeAsync(context);
            await Task.Delay(50, ct);
        }

        return await firstObject;
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

    /// <summary>
    /// Wraps a transport so a test can see every connection a dial opened — and whether the code
    /// under test gave each one back.
    /// </summary>
    private sealed class TrackingQuicTransport : IQuicTransport
    {
        private readonly IQuicTransport _inner;
        private readonly List<TrackingQuicConnection> _connections = [];

        internal TrackingQuicTransport(IQuicTransport inner) => _inner = inner;

        internal IReadOnlyList<TrackingQuicConnection> Connections => _connections;

        public bool IsSupported => _inner.IsSupported;

        public ValueTask<IQuicServer> ListenAsync(QuicServerOptions options,
            CancellationToken cancellationToken = default) => _inner.ListenAsync(options, cancellationToken);

        public async ValueTask<IQuicConnection> ConnectAsync(QuicClientOptions options,
            CancellationToken cancellationToken = default)
        {
            var connection = new TrackingQuicConnection(await _inner.ConnectAsync(options, cancellationToken));
            _connections.Add(connection);
            return connection;
        }
    }

    private sealed class TrackingQuicConnection : IQuicConnection
    {
        private readonly IQuicConnection _inner;

        internal TrackingQuicConnection(IQuicConnection inner) => _inner = inner;

        internal bool IsDisposed { get; private set; }

        public EndPoint RemoteEndPoint => _inner.RemoteEndPoint;

        public SslApplicationProtocol NegotiatedApplicationProtocol => _inner.NegotiatedApplicationProtocol;

        public ValueTask<IQuicStream> OpenStreamAsync(QuicStreamDirection direction,
            CancellationToken cancellationToken = default) => _inner.OpenStreamAsync(direction, cancellationToken);

        public ValueTask<IQuicStream> AcceptStreamAsync(CancellationToken cancellationToken = default) =>
            _inner.AcceptStreamAsync(cancellationToken);

        public ValueTask CloseAsync(long errorCode, CancellationToken cancellationToken = default) =>
            _inner.CloseAsync(errorCode, cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return _inner.DisposeAsync();
        }
    }
}
