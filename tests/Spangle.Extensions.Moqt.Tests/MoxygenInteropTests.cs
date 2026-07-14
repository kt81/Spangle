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

    /// <summary>
    /// A cenzano media-interop GoP through the reference relay: a keyframe (carrying avcC
    /// extradata) opens a group and two delta frames follow as objects within it. Verifies the
    /// relay forwards the per-frame Extension Headers and AVCC payloads intact, and that the
    /// group/object numbering survives — i.e. our MI mapping is on the wire correctly.
    /// </summary>
    [Fact]
    public async Task MediaInteropFrames_SurviveTheRelay()
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

        FullTrackName track = FullTrackName.FromStrings(["vc"], MoqMediaInterop.TrackName("mi/", isAudio: false));
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
        byte[] keyFrame = Pattern(0x11, 96);
        byte[] delta1 = Pattern(0x22, 48);
        byte[] delta2 = Pattern(0x33, 32);

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        await using var mi = new MoqMediaInteropTrack(publisher.PublishTrack(track));
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        Task<IReadOnlyList<MoqObject>> receiving = CollectObjectsAsync(subscriber, track, expected: 3, ct);

        const ulong timebase = 90_000;
        await mi.PublishFrameAsync(keyFrame,
            MoqMediaInterop.VideoH264AvccExtensions(0, 0, 0, timebase, 3_000, 1_700_000_000_000, avcC),
            startsGroup: true, cancellationToken: ct);
        await mi.PublishFrameAsync(delta1,
            MoqMediaInterop.VideoH264AvccExtensions(1, 3_000, 3_000, timebase, 3_000, 1_700_000_000_033),
            startsGroup: false, cancellationToken: ct);
        await mi.PublishFrameAsync(delta2,
            MoqMediaInterop.VideoH264AvccExtensions(2, 6_000, 6_000, timebase, 3_000, 1_700_000_000_066),
            startsGroup: false, cancellationToken: ct);
        await mi.CompleteGroupAsync(ct);

        IReadOnlyList<MoqObject> got = await receiving;
        _output.WriteLine($"relayed {got.Count} MI frames; group ids: {string.Join(",", got.Select(o => o.GroupId))}");

        got.Should().HaveCount(3);
        got.Select(o => o.GroupId).Should().AllBeEquivalentTo(0UL, "one keyframe means one group");
        got.Select(o => o.ObjectId).Should().Equal([0UL, 1UL, 2UL], "frames are objects within the group");

        got[0].Payload.ToArray().Should().Equal(keyFrame);
        got[1].Payload.ToArray().Should().Equal(delta1);
        got[2].Payload.ToArray().Should().Equal(delta2);

        // The keyframe carries media type + avcC + metadata; the deltas carry media type + metadata.
        // Extension headers ride in ascending type order (their types are delta-encoded), so the
        // avcC extradata (0x0D) precedes the packed metadata (0x15) regardless of insertion order.
        got[0].Extensions.Should().HaveCount(3);
        got[0].Extensions.Select(e => e.Type).Should().Equal([0x0AUL, 0x0DUL, 0x15UL]);
        got[0].Extensions[1].Bytes.ToArray().Should().Equal(avcC, "avcC reaches the subscriber verbatim");
        MoqMediaInterop.UnpackVarints(got[0].Extensions[2].Bytes, 6)
            .Should().Equal(0UL, 0UL, 0UL, timebase, 3_000UL, 1_700_000_000_000UL);

        got[1].Extensions.Should().HaveCount(2);
        MoqMediaInterop.UnpackVarints(got[1].Extensions[1].Bytes, 6)
            .Should().Equal(1UL, 3_000UL, 3_000UL, timebase, 3_000UL, 1_700_000_000_033UL);

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

    private static async Task<IReadOnlyList<MoqObject>> CollectObjectsAsync(MoqSubscriber subscriber,
        FullTrackName track, int expected, CancellationToken ct)
    {
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);
        var received = new List<MoqObject>();
        await foreach (MoqObject moqObject in subscription.ReadObjectsAsync(ct))
        {
            received.Add(moqObject);
            if (received.Count == expected)
            {
                break;
            }
        }

        return received;
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

    /// <summary>
    /// Control messages the reference relay must be able to parse. moxygen closes the session with
    /// a PROTOCOL_VIOLATION on anything it cannot decode, so a well-formed reply — success or a
    /// semantic error — proves our encoding is right. Covers TRACK_STATUS and SUBSCRIBE_NAMESPACE
    /// requests, plus decoding whichever of REQUEST_OK / REQUEST_ERROR the relay answers with.
    /// </summary>
    [Fact]
    public async Task ControlMessages_AreParsedByTheRelay()
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

        await using IQuicConnection conn = await ConnectAsync(remote, ct);
        await using MoqSession session = await MoqSession.ConnectAsync(conn, RelaySetup(), ct);

        // TRACK_STATUS: draft-18 says its format is identical to SUBSCRIBE. The relay knows no such
        // track, so it should answer REQUEST_ERROR — which still proves it parsed our request.
        (ulong statusType, byte[] statusPayload) = await RequestAsync(session, MoqControlMessageType.TrackStatus,
            new TrackStatusMessage(0, FullTrackName.FromStrings(["vc"], "nosuchtrack")).EncodePayload, ct);
        _output.WriteLine($"TRACK_STATUS -> 0x{statusType:X}");
        statusType.Should().BeOneOf([MoqControlMessageType.RequestOk, MoqControlMessageType.RequestError],
            "the relay parsed TRACK_STATUS and answered it");
        if (statusType == MoqControlMessageType.RequestError)
        {
            RequestErrorMessage error = RequestErrorMessage.DecodePayload(statusPayload);
            _output.WriteLine($"  REQUEST_ERROR code={error.ErrorCode} reason='{error.ErrorReason}'");
        }
        else
        {
            _ = RequestOkMessage.DecodePayload(statusPayload);
        }

        // SUBSCRIBE_NAMESPACE: asks to be told about namespaces under a prefix.
        (ulong nsType, byte[] nsPayload) = await RequestAsync(session, MoqControlMessageType.SubscribeNamespace,
            new SubscribeNamespaceMessage(2, TrackNamespace.FromStrings("vc")).EncodePayload, ct);
        _output.WriteLine($"SUBSCRIBE_NAMESPACE -> 0x{nsType:X}");
        nsType.Should().BeOneOf([MoqControlMessageType.RequestOk, MoqControlMessageType.RequestError],
            "the relay parsed SUBSCRIBE_NAMESPACE and answered it");
        if (nsType == MoqControlMessageType.RequestOk)
        {
            _ = RequestOkMessage.DecodePayload(nsPayload);
        }
        else
        {
            RequestErrorMessage error = RequestErrorMessage.DecodePayload(nsPayload);
            _output.WriteLine($"  REQUEST_ERROR code={error.ErrorCode} reason='{error.ErrorReason}'");
        }
    }

    /// <summary>Sends one request on its own bidi stream and reads the reply off the same stream.</summary>
    private static async Task<(ulong Type, byte[] Payload)> RequestAsync(MoqSession session, ulong messageType,
        Action<MoqWriter> encodePayload, CancellationToken ct)
    {
        IQuicStream stream = await session.OpenRequestStreamAsync(ct);
        await using (stream.ConfigureAwait(false))
        {
            var payload = new ArrayBufferWriter<byte>();
            encodePayload(new MoqWriter(payload));
            var frame = new ArrayBufferWriter<byte>();
            ControlMessage.Write(frame, messageType, payload.WrittenSpan);
            await stream.WriteAsync(frame.WrittenMemory, completeWrites: false, ct);
            return await ControlMessage.ReadAsync(stream, ct);
        }
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

}
