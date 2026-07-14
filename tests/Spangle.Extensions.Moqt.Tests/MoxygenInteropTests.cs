using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Moqt.Wire;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.MsQuic;
using Xunit.Abstractions;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// Live interop against a running moxygen relay (ghcr.io/facebookexperimental/moqrelay). Gated on
/// the MOQ_RELAY_ENDPOINT env var (host:port, e.g. 127.0.0.1:4433) so an ordinary test run skips
/// it. Confirms Spangle can complete the draft-18 SETUP handshake with moxygen over raw QUIC
/// (ALPN moqt-18, PATH=/moq) — the smoke test that de-risks the whole relay topology before the
/// ANNOUNCE/publish flow is built.
/// </summary>
public class MoxygenInteropTests
{
    private readonly ITestOutputHelper _output;

    public MoxygenInteropTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Spangle_CompletesSetupHandshake_WithMoxygenRelayOverRawQuic()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT not set; skipping live moxygen interop.");
            return;
        }

        if (!MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("msquic not supported on this host; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct = cts.Token;

        await using IQuicConnection conn = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = remote,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = "localhost",
            AllowUntrustedCertificates = true,
        }, ct);
        _output.WriteLine($"QUIC connected to {remote} with ALPN {MoqtConstants.Alpn}.");

        // Over raw QUIC, moxygen's endpoint (-endpoint /moq) is conveyed as the PATH setup option.
        var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path, Encoding.UTF8.GetBytes("/moq"))]);
        await using MoqSession session = await MoqSession.ConnectAsync(conn, setup, ct);

        _output.WriteLine($"SETUP handshake completed. Remote SETUP carries {session.RemoteSetup.Options.Count} option(s):");
        foreach (MoqKeyValuePair option in session.RemoteSetup.Options)
        {
            string value = option.IsBytes
                ? Encoding.UTF8.GetString(option.Bytes)
                : option.VarintValue.ToString(CultureInfo.InvariantCulture);
            _output.WriteLine(FormattableString.Invariant($"  0x{option.Type:X}: {value}"));
        }

        session.RemoteSetup.Options.Should().NotBeNull("a completed SETUP yields the peer's options");
    }

    [Fact]
    public async Task Spangle_AnnouncesNamespace_ToMoxygenRelay()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint) || !MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT unset or msquic unsupported; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct = cts.Token;

        await using IQuicConnection conn = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = remote,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = "localhost",
            AllowUntrustedCertificates = true,
        }, ct);

        var setup = new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path, Encoding.UTF8.GetBytes("/moq"))]);
        await using MoqSession session = await MoqSession.ConnectAsync(conn, setup, ct);
        _output.WriteLine("SETUP done; announcing namespace 'vc'.");

        MoqPublisher publisher = MoqPublisher.Create(session);
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        _output.WriteLine("PUBLISH_NAMESPACE accepted (REQUEST_OK) by the relay.");
    }

    [Fact]
    public async Task Media_FlowsThroughRelay_PublisherToSubscriber()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint) || !MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT unset or msquic unsupported; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        FullTrackName track = FullTrackName.FromStrings(["vc"], "video0");
        byte[] init = Pattern(0x11, 120);
        byte[] segment0 = Pattern(0x22, 400);

        // Publisher: announce the namespace, then serve the relay's forwarded SUBSCRIBE.
        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        var bridge = new CmafMoqTrackBridge(publisher.PublishTrack(track));
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);
        _output.WriteLine("publisher: announced 'vc', serving subscriptions.");

        // Subscriber: a second connection that subscribes to vc/video0 through the relay.
        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        Task<IReadOnlyList<byte[]>> collecting = CollectPayloadsAsync(subscriber, track, expected: 2, ct);

        // The bridge blocks until the relay's SUBSCRIBE reaches us and a subscriber attaches.
        await bridge.PublishInitAsync(init, ct);
        await bridge.PublishSegmentAsync(segment0, ct);
        _output.WriteLine("publisher: streamed init + 1 segment.");

        IReadOnlyList<byte[]> received = await collecting;
        received.Should().HaveCount(2, "the init and one segment arrive through the relay");
        received[0].Should().Equal(init, "the init round-trips through the relay byte for byte");
        received[1].Should().Equal(segment0);

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

    /// <summary>
    /// Object Extension Headers through the reference relay: moxygen parses every object it
    /// forwards, so if our length-prefixed Key-Value-Pair block survives the round trip byte for
    /// byte, our encoding matches the reference implementation's. This is the conformance check
    /// for the draft-cenzano-moq-media-interop metadata carrier.
    /// </summary>
    [Fact]
    public async Task ObjectExtensionHeaders_SurviveTheRelay()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint) || !MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT unset or msquic unsupported; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        FullTrackName track = FullTrackName.FromStrings(["vc"], "ext0");

        // A cenzano-shaped extension set: media type (even -> varint), extradata and packed
        // metadata (odd -> byte strings).
        MoqKeyValuePair[] extensions =
        [
            MoqKeyValuePair.Varint(0x0A, 0x00),
            MoqKeyValuePair.FromBytes(0x0D, [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1]),
            MoqKeyValuePair.FromBytes(0x15, [0x00, 0x2A, 0x2A, 0x40, 0x01]),
        ];
        byte[] frame = Pattern(0x55, 64);

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        MoqPublishedTrack published = publisher.PublishTrack(track);
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        Task<MoqObject> receiving = FirstObjectAsync(subscriber, track, ct);

        MoqGroupWriter group = await published.BeginGroupAsync(0, 100, hasExtensions: true, cancellationToken: ct);
        await using (group.ConfigureAwait(false))
        {
            await group.WriteObjectAsync(0, frame, extensions, ct);
            await group.CompleteAsync(ct);
        }

        MoqObject received = await receiving;
        _output.WriteLine($"relayed object: len={received.Payload.Length} extensions={received.Extensions.Count}");

        received.Payload.ToArray().Should().Equal(frame);
        received.Extensions.Should().HaveCount(3, "the relay forwarded every extension header");
        received.Extensions[0].Type.Should().Be(0x0AUL);
        received.Extensions[0].VarintValue.Should().Be(0x00UL);
        received.Extensions[1].Type.Should().Be(0x0DUL);
        received.Extensions[1].Bytes.ToArray().Should().Equal([0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1]);
        received.Extensions[2].Type.Should().Be(0x15UL);
        received.Extensions[2].Bytes.ToArray().Should().Equal([0x00, 0x2A, 0x2A, 0x40, 0x01]);

        await runCts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // demux loop cancelled
        }
    }

    private static async Task<MoqObject> FirstObjectAsync(MoqSubscriber subscriber, FullTrackName track,
        CancellationToken ct)
    {
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);
        await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct))
        {
            return moqObject;
        }

        throw new InvalidOperationException("no object arrived through the relay");
    }

    private static async Task<IQuicConnection> ConnectAsync(IPEndPoint remote, CancellationToken ct) =>
        await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = remote,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = "localhost",
            AllowUntrustedCertificates = true,
        }, ct);

    private static SetupMessage RelaySetup() =>
        new([MoqKeyValuePair.FromBytes(MoqSetupOption.Path, Encoding.UTF8.GetBytes("/moq"))]);

    private static byte[] Pattern(byte seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }

    private static async Task<IReadOnlyList<byte[]>> CollectPayloadsAsync(
        MoqSubscriber subscriber, FullTrackName track, int expected, CancellationToken ct)
    {
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);
        var received = new List<byte[]>();
        await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct))
        {
            received.Add(moqObject.Payload.ToArray());
            if (received.Count == expected)
            {
                break;
            }
        }

        return received;
    }

    /// <summary>
    /// Cross-draft probe: the browser encoder (moq-encoder-player) publishes at draft-16, Spangle
    /// subscribes at draft-18. If objects arrive, the relay translates between the two drafts and
    /// a draft-18 Spangle can feed a draft-16 browser player. Set MOQ_BROWSER_TRACK to the
    /// encoder's video track (e.g. "20260714152057video0") while the encoder is publishing.
    /// </summary>
    [Fact]
    public async Task Subscribe_ToBrowserEncoderTrack_AcrossDrafts()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        string? trackName = Environment.GetEnvironmentVariable("MOQ_BROWSER_TRACK");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(trackName)
            || !MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT / MOQ_BROWSER_TRACK unset; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        await using IQuicConnection conn = await ConnectAsync(remote, ct);
        await using MoqSession session = await MoqSession.ConnectAsync(conn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(session);

        FullTrackName track = FullTrackName.FromStrings(["vc"], trackName);
        _output.WriteLine($"subscribing (draft-18) to vc/{trackName} published by the draft-16 browser...");
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);
        _output.WriteLine($"SUBSCRIBE_OK, trackAlias={subscription.TrackAlias}");

        var seen = 0;
        await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct))
        {
            ReadOnlySpan<byte> head = moqObject.Payload.Span[..Math.Min(16, moqObject.Payload.Length)];
            _output.WriteLine(FormattableString.Invariant(
                $"  obj g={moqObject.GroupId} o={moqObject.ObjectId} len={moqObject.Payload.Length} head={Convert.ToHexString(head)}"));
            if (++seen >= 5)
            {
                break;
            }
        }

        seen.Should().BeGreaterThan(0, "objects from the draft-16 browser encoder reach a draft-18 subscriber");
    }

    [Fact]
    public async Task Dump_MoxygenControlStreamRawBytes()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint) || !MsQuicTransport.Shared.IsSupported)
        {
            _output.WriteLine("MOQ_RELAY_ENDPOINT unset or msquic unsupported; skipping.");
            return;
        }

        string[] parts = endpoint.Split(':');
        var remote = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken ct = cts.Token;

        await using IQuicConnection conn = await MsQuicTransport.Shared.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = remote,
            ApplicationProtocols = [new SslApplicationProtocol(MoqtConstants.Alpn)],
            TargetHost = "localhost",
            AllowUntrustedCertificates = true,
        }, ct);

        // Send our control stream (uni) + SETUP so moxygen keeps the session alive while we read.
        IQuicStream outbound = await conn.OpenStreamAsync(QuicStreamDirection.Unidirectional, ct);
        var payload = new ArrayBufferWriter<byte>();
        new SetupMessage([MoqKeyValuePair.FromBytes(MoqSetupOption.Path, Encoding.UTF8.GetBytes("/moq"))])
            .EncodePayload(new MoqWriter(payload));
        var frame = new ArrayBufferWriter<byte>();
        ControlMessage.Write(frame, MoqControlMessageType.Setup, payload.WrittenSpan);
        await outbound.WriteAsync(frame.WrittenMemory, completeWrites: false, ct);
        _output.WriteLine("OUR control stream bytes: " + Convert.ToHexString(frame.WrittenSpan));

        // Dump what moxygen sends on its inbound control stream (accumulate a few reads).
        await using IQuicStream inbound = await conn.AcceptStreamAsync(ct);
        var all = new List<byte>();
        var buf = new byte[512];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (all.Count < 400)
            {
                int n = await inbound.ReadAsync(buf, readCts.Token);
                if (n == 0)
                {
                    break;
                }

                all.AddRange(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // stop reading after the short window
        }

        _output.WriteLine($"MOXYGEN control stream {all.Count} bytes: " + Convert.ToHexString(all.ToArray()));
    }
}
