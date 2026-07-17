using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using Spangle.Net.Moqt;
using Spangle.Net.Moqt.Messages;
using Spangle.Net.Transport.Quic;
using Spangle.Net.Transport.Quic.InMemory;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt.Tests;

/// <summary>
/// Ingest end to end, in process: a real <see cref="MoqPublisher"/> publishes a catalog and a LOC
/// video track, a <see cref="MoqReceiverContext"/> discovers and pulls it, and the MediaFrames it
/// produces are checked against what was published. This is the whole ingest path except the dial —
/// catalog discovery, codec mapping, the codec-event wiring the pipeline depends on, and the
/// LOC → MediaFrame decode — over the in-memory transport, so it runs anywhere.
/// </summary>
public class MoqIngestRoundTripTests
{
    private static readonly SslApplicationProtocol Alpn = new(MoqtConstants.Alpn);

    private readonly record struct Frame(MediaFrameKind Kind, MediaFrameFlags Flags, uint Codec, byte[] Payload);

    [Fact]
    public async Task ACatalogAndAGoP_AreDiscoveredAndDecodedToMediaFrames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken ct = cts.Token;

        var transport = new InMemoryQuicTransport();
        await using IQuicServer server = await transport.ListenAsync(new QuicServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [Alpn],
        }, ct);

        ValueTask<IQuicConnection> acceptTask = server.AcceptConnectionAsync(ct);
        await using IQuicConnection clientConn = await transport.ConnectAsync(new QuicClientOptions
        {
            RemoteEndPoint = server.LocalEndPoint,
            ApplicationProtocols = [Alpn],
        }, ct);
        await using IQuicConnection serverConn = await acceptTask;

        // Publisher on the server side; ingest (subscriber) on the client side.
        Task<MoqSession> pubSessionTask = MoqSession.AcceptAsync(serverConn, new SetupMessage(), ct);
        await using MoqSession subSession = await MoqSession.ConnectAsync(clientConn, new SetupMessage(), ct);
        await using MoqSession pubSession = await pubSessionTask;

        string[] ns = ["vc", "test"];
        byte[] avcC = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x04];
        byte[] key = Pattern(0x11, 96);
        byte[] delta = Pattern(0x22, 48);

        MoqPublisher publisher = MoqPublisher.Create(pubSession);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task publisherTask = RunPublisherAsync(publisher, ns, avcC, key, delta, runCts.Token);

        // The receiver, driven as LiveContext would drive it: on the codec event, wire a MediaOutlet.
        var pipe = new Pipe();
        using var receiver = new MoqReceiverContext(subSession, ns, "test",
            new IPEndPoint(IPAddress.Loopback, 4433), LocDraft.Draft03, ct);
        receiver.VideoCodecSet += _ => receiver.MediaOutlet = pipe.Writer;

        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiver.CancellationToken = receiveCts.Token;
        Task receiveTask = receiver.BeginReceiveAsync(receiveCts).AsTask();

        // Collect until the keyframe and its delta have both come through as MediaFrames.
        IReadOnlyList<Frame> frames = await CollectFramesAsync(pipe.Reader, until: 3, ct);

        frames.Should().HaveCount(3, "the config, then the keyframe and the delta");
        frames[0].Flags.Should().Be(MediaFrameFlags.Config);
        frames[0].Codec.Should().Be((uint)VideoCodec.H264, "avc1.64001f in the catalog maps to H.264");
        frames[0].Payload.Should().Equal(avcC, "the avcC survives egress and ingest");
        frames[1].Flags.Should().Be(MediaFrameFlags.KeyFrame);
        frames[1].Payload.Should().Equal(key);
        frames[2].Flags.Should().Be(MediaFrameFlags.None);
        frames[2].Payload.Should().Equal(delta);

        receiver.StreamName.Should().Be("test");

        await receiveCts.CancelAsync();
        await runCts.CancelAsync();
        await SwallowAsync(receiveTask);
        await SwallowAsync(publisherTask);
    }

    private static async Task RunPublisherAsync(MoqPublisher publisher, string[] ns, byte[] avcC,
        byte[] key, byte[] delta, CancellationToken ct)
    {
        var @namespace = TrackNamespace.FromStrings(ns);
        await using var catalog = new MoqCatalogTrack(publisher.PublishTrack(MoqCatalogTrack.NameIn(@namespace)));
        await using var video = new MoqFrameTrack(publisher.PublishTrack(FullTrackName.FromStrings(ns, "video0")));
        Task run = publisher.RunAsync(ct);

        MsfCatalog catalogDoc = new()
        {
            Tracks =
            [
                new MsfTrack
                {
                    Name = "video0", Packaging = MsfPackaging.Loc, IsLive = true, Role = MsfTrackRole.Video,
                    Codec = "avc1.64001f", Width = 640, Height = 360,
                },
            ],
        };

        // Republish the catalog until cancelled, so the ingest sees one whenever it subscribes.
        Task catalogLoop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await catalog.PublishAsync(catalogDoc, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        // One GoP: a keyframe carrying its avcC, then a delta.
        const ulong timescale = 90_000;
        await video.PublishFrameAsync(key,
            [.. Loc03Properties.MediaTime(0, timescale), Loc03Properties.VideoConfig(avcC)],
            startsGroup: true, cancellationToken: ct).ConfigureAwait(false);
        await video.PublishFrameAsync(delta, Loc03Properties.MediaTime(3_000, timescale),
            startsGroup: false, cancellationToken: ct).ConfigureAwait(false);

        // Keep the frames' group open (and the catalog flowing) until the test is done reading.
        await SwallowAsync(Task.WhenAll(run, catalogLoop)).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Frame>> CollectFramesAsync(PipeReader reader, int until,
        CancellationToken ct)
    {
        var frames = new List<Frame>();
        while (frames.Count < until)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            byte[] all = result.Buffer.ToArray();
            var offset = 0;
            frames.Clear();
            while (offset + MediaFrameHeader.Size <= all.Length)
            {
                MediaFrameHeader header =
                    MemoryMarshal.Read<MediaFrameHeader>(all.AsSpan(offset, MediaFrameHeader.Size));
                offset += MediaFrameHeader.Size;
                if (offset + header.Length > all.Length)
                {
                    break; // a frame straddles the buffer edge; wait for the rest
                }

                byte[] payload = all.AsSpan(offset, header.Length).ToArray();
                offset += header.Length;
                frames.Add(new Frame(header.Kind, header.Flags, header.Codec, payload));
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        return frames;
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
#pragma warning disable VSTHRD003 // tasks created within this test
            await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // cancellation and teardown races are expected here
        }
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
}
