using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Data;
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

    // Throws xunit's skip exception when no live relay is configured, so the gate shows up in
    // the test report as a skip with its reason — a silent 'return' counted as a pass, and a
    // CI run that never touched the relay was indistinguishable from one that did. Also the
    // one place the endpoint string is parsed ('host:port', IPv4).
    private static IPEndPoint RequireRelay()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MOQ_RELAY_ENDPOINT");
        Skip.If(string.IsNullOrWhiteSpace(endpoint), "MOQ_RELAY_ENDPOINT not set; live moxygen interop skipped.");
        Skip.IfNot(MsQuicTransport.Shared.IsSupported, "msquic is not supported on this host.");

        string[] parts = endpoint!.Split(':');
        return new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task Spangle_CompletesSetupHandshake_WithMoxygenRelayOverRawQuic()
    {
        IPEndPoint remote = RequireRelay();

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

    [SkippableFact]
    public async Task Spangle_AnnouncesNamespace_ToMoxygenRelay()
    {
        IPEndPoint remote = RequireRelay();
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

    [SkippableFact]
    public async Task Media_FlowsThroughRelay_PublisherToSubscriber()
    {
        IPEndPoint remote = RequireRelay();
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
    /// Object Extension Headers through the reference relay: the relay parses every object it
    /// forwards, so if our length-prefixed Key-Value-Pair block survives the round trip byte for
    /// byte, our encoding matches the reference implementation's. This is the conformance check for
    /// the carrier every media mapping puts its per-frame metadata in — LOC included.
    /// </summary>
    [SkippableFact]
    public async Task ObjectExtensionHeaders_SurviveTheRelay()
    {
        IPEndPoint remote = RequireRelay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        FullTrackName track = FullTrackName.FromStrings(["vc"], "ext0");

        // Both value shapes the parity rule allows, so the round trip exercises each: an even type
        // carries a varint, an odd one a length-prefixed byte string.
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
    /// A LOC GoP through the reference relay: a keyframe (carrying its Video Config) opens a group
    /// and two delta frames follow as objects within it. Verifies the relay forwards the per-frame
    /// LOC Properties and the elementary bitstream intact, and that the group/object numbering
    /// survives — i.e. our LOC mapping is on the wire correctly.
    /// </summary>
    [SkippableFact]
    public async Task LocFrames_SurviveTheRelay()
    {
        IPEndPoint remote = RequireRelay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        FullTrackName track = FullTrackName.FromStrings(["vc"], "loc/video0");
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
        byte[] keyFrame = Pattern(0x11, 96);
        byte[] delta1 = Pattern(0x22, 48);
        byte[] delta2 = Pattern(0x33, 32);

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        await using var loc = new MoqFrameTrack(publisher.PublishTrack(track));
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        Task<IReadOnlyList<MoqObject>> receiving = CollectObjectsAsync(subscriber, track, expected: 3, ct);

        const ulong timescale = 90_000;
        await loc.PublishFrameAsync(keyFrame,
            [.. Loc03Properties.MediaTime(0, timescale), Loc03Properties.VideoConfig(avcC)],
            startsGroup: true, cancellationToken: ct);
        await loc.PublishFrameAsync(delta1, Loc03Properties.MediaTime(3_000, timescale),
            startsGroup: false, cancellationToken: ct);
        await loc.PublishFrameAsync(delta2, Loc03Properties.MediaTime(6_000, timescale),
            startsGroup: false, cancellationToken: ct);
        await loc.CompleteGroupAsync(ct);

        IReadOnlyList<MoqObject> got = await receiving;
        _output.WriteLine($"relayed {got.Count} LOC frames; group ids: {string.Join(",", got.Select(o => o.GroupId))}");

        got.Should().HaveCount(3);
        got.Select(o => o.GroupId).Should().AllBeEquivalentTo(0UL, "one keyframe means one group");
        got.Select(o => o.ObjectId).Should().Equal([0UL, 1UL, 2UL], "frames are objects within the group");

        // The payload is the elementary bitstream and nothing else — that is the whole of LOC's
        // low overhead, so a relay that added or trimmed a byte would show up right here.
        got[0].Payload.ToArray().Should().Equal(keyFrame);
        got[1].Payload.ToArray().Should().Equal(delta1);
        got[2].Payload.ToArray().Should().Equal(delta2);

        // Only the keyframe carries a Video Config. Properties ride in ascending ID order (the IDs
        // are delta-encoded on the wire), so Timescale 0x08, Timestamp 0x0A, Video Config 0x0D.
        got[0].Extensions.Select(e => e.Type).Should().Equal([0x08UL, 0x0AUL, 0x0DUL]);
        got[1].Extensions.Select(e => e.Type).Should().Equal([0x08UL, 0x0AUL]);

        Loc03Metadata key = Loc03Metadata.Read(got[0].Extensions);
        key.Timestamp.Should().Be(0UL);
        key.Timescale.Should().Be(timescale);
        key.IsWallClock.Should().BeFalse("a Timescale came with the Timestamp, so this is media time");
        key.VideoConfig.ToArray().Should().Equal(avcC, "the avcC reaches the subscriber verbatim");

        Loc03Metadata second = Loc03Metadata.Read(got[1].Extensions);
        second.Timestamp.Should().Be(3_000UL);
        second.Timescale.Should().Be(timescale);
        second.VideoConfig.IsEmpty.Should().BeTrue("only a keyframe re-sends the decoder configuration");

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
    /// Two tracks on one session, each with its own subscriber. Every other test here publishes a
    /// single track, which is the case where the first subscription's Track Alias is also the only
    /// one — so nothing has ever exercised a publisher assigning a second alias. A real stream is at
    /// least a catalog and a video track, and a player subscribes to both.
    /// </summary>
    [SkippableFact]
    public async Task TwoTracks_EachReachTheirSubscriber()
    {
        IPEndPoint remote = RequireRelay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        // Distinct names per run: a relay caches by track, and a track it has seen before brings its
        // cache with it.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        FullTrackName first = FullTrackName.FromStrings(["vc"], $"two/{suffix}/a");
        FullTrackName second = FullTrackName.FromStrings(["vc"], $"two/{suffix}/b");
        byte[] payloadA = Pattern(0x11, 64);
        byte[] payloadB = Pattern(0x22, 64);

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        MoqPublishedTrack trackA = publisher.PublishTrack(first);
        MoqPublishedTrack trackB = publisher.PublishTrack(second);
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);

        Task<IReadOnlyList<MoqObject>> receivingA = CollectObjectsAsync(subscriber, first, expected: 1, ct);
        await WriteOneAsync(trackA, payloadA, ct);
        (await receivingA)[0].Payload.ToArray().Should().Equal(payloadA, "the first track's subscriber gets its object");
        _output.WriteLine($"track a: alias {trackA.CurrentAlias}, delivered.");

        // The second subscription is the one nothing has covered: it gets the publisher's next alias.
        Task<IReadOnlyList<MoqObject>> receivingB = CollectObjectsAsync(subscriber, second, expected: 1, ct);
        await WriteOneAsync(trackB, payloadB, ct);
        _output.WriteLine($"track b: alias {trackB.CurrentAlias}, awaiting delivery...");
        (await receivingB)[0].Payload.ToArray().Should().Equal(payloadB,
            "a publisher's second track reaches its subscriber too");

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

    private static async Task WriteOneAsync(MoqPublishedTrack track, byte[] payload, CancellationToken ct)
    {
        MoqGroupWriter group = await track.BeginGroupAsync(0, 128, endOfGroup: true, cancellationToken: ct);
        await using (group.ConfigureAwait(false))
        {
            await group.WriteObjectAsync(0, payload, cancellationToken: ct);
            await group.CompleteAsync(ct);
        }
    }

    /// <summary>
    /// END_OF_GROUP through the reference relay, on both stream mappings. The relay re-encodes every
    /// subgroup header it forwards, so whether the bit reaches a subscriber is a real question and
    /// not a formality — and the answer decides whether a player stalls: it marks a group complete
    /// only on this signal, and otherwise waits out a timeout before playing what came after.
    /// </summary>
    [SkippableTheory]
    [InlineData(MoqStreamMapping.GroupPerStream)]
    [InlineData(MoqStreamMapping.ObjectPerStream)]
    public async Task EndOfGroup_SurvivesTheRelay(MoqStreamMapping mapping)
    {
        IPEndPoint remote = RequireRelay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        FullTrackName track = FullTrackName.FromStrings(["vc"], $"eog/{mapping}");
        byte[] keyFrame = Pattern(0x11, 96);
        byte[] delta = Pattern(0x22, 48);

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        await using var loc = new MoqFrameTrack(publisher.PublishTrack(track), mapping);
        await publisher.AnnounceNamespaceAsync(TrackNamespace.FromStrings("vc"), ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        await using MoqSubscription subscription = await subscriber.SubscribeAsync(track, ct);

        // One subgroup per group under the default mapping; one per object plus a closing marker
        // under the mapping MSF §6 asks for.
        int expectedSubgroups = mapping == MoqStreamMapping.GroupPerStream ? 1 : 3;
        Task<IReadOnlyList<ReceivedSubgroup>> receiving =
            CollectSubgroupsAsync(subConn, subscription.TrackAlias, expectedSubgroups, ct);

        await loc.PublishFrameAsync(keyFrame, Loc03Properties.MediaTime(0), startsGroup: true, cancellationToken: ct);
        await loc.PublishFrameAsync(delta, Loc03Properties.MediaTime(3_000), startsGroup: false, cancellationToken: ct);
        await loc.CompleteGroupAsync(ct);

        IReadOnlyList<ReceivedSubgroup> got = await receiving;
        foreach (ReceivedSubgroup subgroup in got)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"subgroup g={subgroup.Header.GroupId} sg={subgroup.Header.SubgroupId} endOfGroup={subgroup.Header.EndOfGroup} objects={subgroup.Objects.Count}"));
        }

        if (mapping == MoqStreamMapping.GroupPerStream)
        {
            ReceivedSubgroup only = got.Should().ContainSingle().Subject;

            // The bit was set when the stream was opened (proved in process, in the Moqt package's
            // own tests) and is gone by the time it reaches a subscriber: the relay re-encodes the
            // header and drops it. That is the whole reason the marker object below exists — and why
            // this is logged rather than asserted, since the day a relay stops dropping it is not a
            // day this test should fail.
            _output.WriteLine($"header END_OF_GROUP survived the relay: {only.Header.EndOfGroup}");

            only.Objects.Select(o => o.ObjectId).Should().Equal([0UL, 1UL, 2UL]);
            only.Objects.Take(2).Should().AllSatisfy(o => o.Status.Should().Be(MoqObjectStatus.Normal));
            only.Objects[2].Status.Should().Be(MoqObjectStatus.EndOfGroup,
                "the group's own stream carries the news that the group ended");
            only.Objects[2].Payload.IsEmpty.Should().BeTrue("the marker is news, not media");
        }
        else
        {
            // Each frame on its own stream, each its own subgroup, and none of them able to claim
            // the group ended — so the group is closed by a subgroup carrying only that news.
            got.Should().HaveCount(3);
            got.Take(2).Should().AllSatisfy(s => s.Header.EndOfGroup.Should().BeFalse(
                "a frame's stream is opened before we know whether the group ends there"));
            got.Select(s => s.Header.SubgroupId).Should().Equal([0UL, 1UL, 2UL], "one subgroup per object");
            got.Select(s => s.Objects.Count).Should().AllBeEquivalentTo(1, "one object per stream is the whole rule");

            ReceivedSubgroup marker = got[2];
            _output.WriteLine($"marker header END_OF_GROUP survived the relay: {marker.Header.EndOfGroup}");
            marker.Objects[0].Status.Should().Be(MoqObjectStatus.EndOfGroup);
            marker.Objects[0].Payload.IsEmpty.Should().BeTrue("the marker is news, not media");
            marker.Objects[0].ObjectId.Should().Be(2UL, "no object at or after this one exists in the group");
        }

        got.Should().AllSatisfy(s => s.Header.GroupId.Should().Be(0UL));

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

    private sealed record ReceivedSubgroup(SubgroupHeader Header, IReadOnlyList<MoqObject> Objects);

    /// <summary>
    /// Collects whole subgroups — header included — rather than the flat object stream
    /// <see cref="MoqSubscription.ReadObjectsAsync"/> yields, because END_OF_GROUP lives in the
    /// header and that is the thing under test.
    /// </summary>
    private static async Task<IReadOnlyList<ReceivedSubgroup>> CollectSubgroupsAsync(IQuicConnection connection,
        ulong alias, int expected, CancellationToken ct)
    {
        var subgroups = new List<ReceivedSubgroup>();
        while (subgroups.Count < expected)
        {
            MoqIncomingStream incoming = await MoqStreamRouter.AcceptAsync(connection, ct);
            if (incoming is not MoqSubgroupStream subgroup || subgroup.Reader.Header.TrackAlias != alias)
            {
                continue;
            }

            var objects = new List<MoqObject>();
            while (await subgroup.Reader.ReadObjectAsync(ct) is { } moqObject)
            {
                objects.Add(moqObject);
            }

            subgroups.Add(new ReceivedSubgroup(subgroup.Reader.Header, objects));
            await subgroup.Stream.DisposeAsync();
        }

        return subgroups;
    }

    /// <summary>
    /// The catalog track through the reference relay. The relay never reads the payload — MSF §5
    /// makes it opaque, which is exactly why this test is about the object mapping and not the JSON:
    /// the "catalog" track name, the independent catalog at Object ID 0 of a new group, subgroup 0,
    /// and a second publish landing in a new group. That mapping is what a subscriber looks for
    /// before it has parsed a byte, and the relay forwarding it means we produce what a player will
    /// go looking for. The document's own conformance is checked against a real MSF parser, not here.
    /// </summary>
    [SkippableFact]
    public async Task Catalog_SurvivesTheRelay()
    {
        IPEndPoint remote = RequireRelay();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = cts.Token;

        TrackNamespace @namespace = TrackNamespace.FromStrings("vc");
        FullTrackName track = MoqCatalogTrack.NameIn(@namespace);
        track.NameAsString.Should().Be("catalog", "MSF §5 fixes the track name a subscriber asks for");

        MsfCatalog first = new()
        {
            GeneratedAt = 1_746_104_606_044,
            Tracks =
            [
                new MsfTrack
                {
                    Name = "video0",
                    Packaging = MsfPackaging.Loc,
                    IsLive = true,
                    Role = MsfTrackRole.Video,
                    Codec = "avc1.64001f",
                    Width = 640,
                    Height = 360,
                },
            ],
        };
        // A second publish, as a real publisher does when the track list changes: audio joins.
        MsfCatalog second = first with
        {
            Tracks =
            [
                .. first.Tracks,
                new MsfTrack
                {
                    Name = "audio0", Packaging = MsfPackaging.Loc, IsLive = true, Role = MsfTrackRole.Audio,
                    Codec = "opus", SampleRate = 48_000, ChannelConfig = "2",
                },
            ],
        };

        await using IQuicConnection pubConn = await ConnectAsync(remote, ct);
        await using MoqSession pubSession = await MoqSession.ConnectAsync(pubConn, RelaySetup(), ct);
        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        await using var catalog = new MoqCatalogTrack(publisher.PublishTrack(track));
        await publisher.AnnounceNamespaceAsync(@namespace, ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = publisher.RunAsync(runCts.Token);

        await using IQuicConnection subConn = await ConnectAsync(remote, ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(subConn, RelaySetup(), ct);
        MoqSubscriber subscriber = MoqSubscriber.Create(subSession);
        // Two catalogs, each followed by the End of Group object that closes its group.
        Task<IReadOnlyList<MoqObject>> receiving = CollectObjectsAsync(subscriber, track, expected: 4, ct);

        await catalog.PublishAsync(first, ct);
        await catalog.PublishAsync(second, ct);

        IReadOnlyList<MoqObject> got = await receiving;
        _output.WriteLine($"relayed: {string.Join(" | ", got.Select(o => o.Status == MoqObjectStatus.Normal
            ? Encoding.UTF8.GetString(o.Payload.Span)
            : $"<{o.Status}>"))}");

        IReadOnlyList<MoqObject> catalogs = [.. got.Where(o => o.Status == MoqObjectStatus.Normal)];
        // §5: an independent catalog is Object ID 0 of a new group, on subgroup 0. Each publish here
        // is independent, so each one opens a group and sits at its head.
        catalogs.Should().HaveCount(2);
        catalogs.Select(o => o.GroupId).Should().Equal([0UL, 1UL], "a new independent catalog starts a new group");
        catalogs.Select(o => o.ObjectId).Should().AllBeEquivalentTo(0UL, "an independent catalog is the group's first object");
        catalogs.Select(o => o.SubgroupId).Should().AllBeEquivalentTo(0UL);

        // The relay strips the header's END_OF_GROUP bit, so this object is all a subscriber has to
        // tell a finished catalog group from one still arriving.
        got.Where(o => o.Status == MoqObjectStatus.EndOfGroup).Should().HaveCount(2, "each group is closed explicitly");

        catalogs[0].Payload.ToArray().Should().Equal(first.ToJsonUtf8(), "the relay forwards the catalog byte for byte");

        MsfCatalog parsed = MsfCatalog.Parse(catalogs[1].Payload.Span, catalogNamespace: "vc");
        parsed.Tracks.Select(t => t.Name).Should().Equal(["video0", "audio0"]);
        parsed.Tracks.Should().AllSatisfy(t => t.Namespace.Should().Be("vc", "tracks inherit the catalog's namespace"));

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
    [SkippableFact]
    public async Task ControlMessages_AreParsedByTheRelay()
    {
        IPEndPoint remote = RequireRelay();
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
            if (moqObject.Status != MoqObjectStatus.Normal)
            {
                continue; // the End of Group object that closes each group carries no media
            }

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
    [SkippableFact]
    public async Task Subscribe_ToBrowserEncoderTrack_AcrossDrafts()
    {
        IPEndPoint remote = RequireRelay();
        string? trackName = Environment.GetEnvironmentVariable("MOQ_BROWSER_TRACK");
        Skip.If(string.IsNullOrWhiteSpace(trackName),
            "MOQ_BROWSER_TRACK not set; the browser-encoder subscription needs a live browser publisher.");
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

            // The extension headers are where the media mapping puts its per-frame metadata, and
            // where our own reading of it is least certain: the packager draft says "varint"
            // without saying which, and the two codecs disagree above 0x3F. Dumping the raw bytes
            // of a real packager's output is the only way to settle it — see MoqMediaInterop.
            foreach (MoqKeyValuePair extension in moqObject.Extensions)
            {
                string value = extension.IsBytes
                    ? $"bytes[{extension.Bytes.Length}]={Convert.ToHexString(extension.Bytes)}"
                    : $"varint={extension.VarintValue}";
                _output.WriteLine(FormattableString.Invariant($"      ext 0x{extension.Type:X2} {value}"));
            }

            if (++seen >= 5)
            {
                break;
            }
        }

        seen.Should().BeGreaterThan(0, "objects from the draft-16 browser encoder reach a draft-18 subscriber");
    }

}
