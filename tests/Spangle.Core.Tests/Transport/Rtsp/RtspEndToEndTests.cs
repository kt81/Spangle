using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtsp;

namespace Spangle.Tests.Transport.Rtsp;

/// <summary>
/// A full RTSP pull ingest against a scripted fake server on loopback: the real
/// <see cref="RtspReceiverContext"/> runs the OPTIONS → DESCRIBE → SETUP → PLAY
/// handshake, receives interleaved (RFC 2326 §10.12) RTP for an H.264 track, and
/// the <see cref="LiveContext"/> republishes it as HLS. It proves the whole ingest
/// path is connected end to end: a playlist and at least one media segment appear
/// in storage. Wired exactly like Spangle.Extensions.Kestrel/RtspIngestService.cs.
/// </summary>
public class RtspEndToEndTests
{
    private const string StreamName = "cam1";
    private const byte NalIdrSlice = 5;
    private const byte NalNonIdrSlice = 1;
    private const byte VideoPayloadType = 96;
    private const int TimestampStep = 3000; // 90 kHz / 30 fps

    [Fact]
    public async Task PullsRtspStreamAndProducesHlsOutput()
    {
        // A hard budget so a bug in the ingest path can never hang CI.
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct = hardTimeout.Token;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = FormattableString.Invariant($"rtsp://127.0.0.1:{port}/stream");

        // Stops the fake server: when cancelled the server hard-closes the socket, which
        // ends the client's read loop cleanly (EOF) and makes its TEARDOWN write fail fast.
        using var stopServer = new CancellationTokenSource();
        Task serverTask = RunFakeServerAsync(listener, stopServer.Token, ct);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port, ct);
        EndPoint endPoint = socket.RemoteEndPoint!;
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        PipeReader reader = PipeReader.Create(stream);
        PipeWriter writer = PipeWriter.Create(stream);

        var storage = new MemoryHLSStorage();
        var receiver = new RtspReceiverContext(reader, writer, url, StreamName, endPoint,
            userName: null, password: null, RtspDialect.Default, ct);
        var hls = new HLSSenderContext(ct)
        {
            Storage = storage,
            SegmentFormat = HLSSegmentFormat.MpegTs, // TS: SPS dimensions are not required
            TargetSegmentDuration = 0.5,
        };
        var live = new LiveContext(receiver, hls, cancellationToken: ct);
        var sender = new HLSSender();

        Task senderTask = Task.Run(() => sender.StartAsync(hls).AsTask(), CancellationToken.None);
        Task liveTask = live.StartAsync().AsTask();

        try
        {
            // Wait for a media segment to appear in the live playlist.
            string? segmentName = await PollForSegmentAsync(storage, ct);
            segmentName.Should().NotBeNull("the RTSP pull must republish at least one HLS media segment");

            storage.TryGetStream(StreamName, out IHLSStreamStorage streamStore).Should().BeTrue();
            streamStore.Playlist.Should().Contain("#EXTM3U", "a valid HLS media playlist was published");
            streamStore.TryReadBlob(segmentName!, out ReadOnlyMemory<byte> segment).Should().BeTrue();
            segment.Length.Should().BeGreaterThan(0, "the segment blob must hold muxed TS data");
        }
        finally
        {
            // Stopping the server hard-closes the socket, so the client's read loop ends
            // and the ingest drains cleanly. Every task shares the 15s hard-timeout token,
            // so awaiting these to completion is bounded and cannot hang; the background
            // tasks are drained before the objects they use are disposed.
            await stopServer.CancelAsync();
            try { await liveTask; } catch (Exception) { /* cancelled/torn-down transport */ }
            try { await senderTask; } catch (Exception) { /* intake completed on shutdown */ }
            try { await serverTask; } catch (Exception) { /* deliberate server close */ }
            sender.Dispose();
            live.Dispose();
        }
    }

    // =======================================================================
    // client-side polling

    private static async Task<string?> PollForSegmentAsync(MemoryHLSStorage storage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (storage.TryGetStream(StreamName, out IHLSStreamStorage stream)
                && stream.Playlist is { } playlist
                && playlist.Contains("#EXTM3U", StringComparison.Ordinal)
                && FirstSegmentName(playlist) is { } name)
            {
                return name;
            }
            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return null;
    }

    private static string? FirstSegmentName(string playlist)
    {
        foreach (string line in playlist.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && trimmed.EndsWith(".ts", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }
        return null;
    }

    // =======================================================================
    // scripted fake RTSP server

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks complete before disposal",
        Justification = "The concurrent streaming task writes to the NetworkStream and SemaphoreSlim; the finally block awaits it to completion before either is disposed, which the analyzer cannot prove across the read loop.")]
    private static async Task RunFakeServerAsync(TcpListener listener, CancellationToken stop, CancellationToken hard)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, hard);
        CancellationToken ct = linked.Token;
        using Socket client = await listener.AcceptSocketAsync(ct);
        // Hard close on shutdown -> RST, so the client's pending TEARDOWN write throws
        // immediately instead of blocking on a response that will never come.
        client.LingerState = new LingerOption(enable: true, seconds: 0);
        var ns = new NetworkStream(client, ownsSocket: false);

        var writeLock = new SemaphoreSlim(1, 1);
        Task streaming = Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RtspRequestHead? head = await ReadRequestHeadAsync(ns, ct);
                if (head is null)
                {
                    break; // client closed
                }

                switch (head.Method)
                {
                    case "OPTIONS":
                        await RespondAsync(ns, writeLock, head.CSeq,
                            "Public: DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n", body: null, ct);
                        break;

                    case "DESCRIBE":
                        byte[] sdp = Encoding.ASCII.GetBytes(BuildSdp());
                        await RespondAsync(ns, writeLock, head.CSeq, "Content-Type: application/sdp\r\n", sdp, ct);
                        break;

                    case "SETUP":
                        // Echo the client's interleaved channels and hand out a session.
                        string transport = head.Transport ?? "RTP/AVP/TCP;unicast;interleaved=0-1";
                        await RespondAsync(ns, writeLock, head.CSeq,
                            FormattableString.Invariant($"Transport: {transport}\r\nSession: 12345678;timeout=60\r\n"),
                            body: null, ct);
                        break;

                    case "PLAY":
                        await RespondAsync(ns, writeLock, head.CSeq,
                            "RTP-Info: url=trackID=0;seq=0;rtptime=0\r\nSession: 12345678\r\n", body: null, ct);
                        streaming = StreamVideoAsync(ns, writeLock, ct);
                        break;

                    case "TEARDOWN":
                        await RespondAsync(ns, writeLock, head.CSeq, extraHeaders: "", body: null, ct);
                        return;

                    default: // GET_PARAMETER / OPTIONS keepalive probes
                        await RespondAsync(ns, writeLock, head.CSeq, extraHeaders: "", body: null, ct);
                        break;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // shutting the server down; the socket is being torn down deliberately
        }
        finally
        {
            // Drain the streaming task before disposing the stream it writes to.
            try { await streaming; } catch (Exception) { /* ends when the socket is closed */ }
            writeLock.Dispose();
            await ns.DisposeAsync();
        }
    }

    /// <summary>Streams interleaved H.264 RTP: an IDR key frame, then P-frames, a key frame every 15.</summary>
    private static async Task StreamVideoAsync(NetworkStream ns, SemaphoreSlim writeLock, CancellationToken ct)
    {
        byte[] body = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17];
        try
        {
            for (ushort seq = 0; !ct.IsCancellationRequested; seq++)
            {
                bool keyFrame = seq % 15 == 0;
                byte nalHeader = (byte)(0x60 | (keyFrame ? NalIdrSlice : NalNonIdrSlice)); // F=0, NRI=3
                byte[] nal = [nalHeader, .. body];
                byte[] rtp = BuildRtpPacket(VideoPayloadType, seq, (uint)(seq * TimestampStep), marker: true, nal);
                byte[] frame = Interleave(channel: 0, rtp);

                await writeLock.WaitAsync(ct);
                try
                {
                    await ns.WriteAsync(frame, ct);
                    await ns.FlushAsync(ct);
                }
                finally
                {
                    writeLock.Release();
                }
                await Task.Delay(5, ct); // ~real time so segments cut on wall-clock too
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // deliberate shutdown
        }
        catch (IOException)
        {
            // the client went away
        }
    }

    // =======================================================================
    // wire building

    private static string BuildSdp()
    {
        // A minimal but valid H.264 baseline SPS/PPS; TS output does not need the dimensions.
        byte[] sps = [0x67, 0x42, 0x00, 0x0A, 0xF8, 0x41, 0xA2];
        byte[] pps = [0x68, 0xCE, 0x38, 0x80];
        string sprop = $"{Convert.ToBase64String(sps)},{Convert.ToBase64String(pps)}";
        return "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=Spangle Test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n" +
            "a=control:*\r\n" +
            "m=video 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={sprop}\r\n" +
            "a=control:trackID=0\r\n";
    }

    private static byte[] BuildRtpPacket(byte payloadType, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[12 + payload.Length];
        packet[0] = 0x80; // V=2
        packet[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x0000ABCDu); // SSRC
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    /// <summary>Interleaved framing (RFC 2326 §10.12): '$' + channel + 16-bit big-endian length + payload.</summary>
    private static byte[] Interleave(byte channel, ReadOnlySpan<byte> rtp)
    {
        byte[] frame = new byte[4 + rtp.Length];
        frame[0] = 0x24;
        frame[1] = channel;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)rtp.Length);
        rtp.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static async Task RespondAsync(NetworkStream ns, SemaphoreSlim writeLock, int cseq,
        string extraHeaders, byte[]? body, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append("RTSP/1.0 200 OK\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"CSeq: {cseq}\r\n");
        sb.Append(extraHeaders);
        if (body is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        }
        sb.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());

        await writeLock.WaitAsync(ct);
        try
        {
            await ns.WriteAsync(headBytes, ct);
            if (body is not null)
            {
                await ns.WriteAsync(body, ct);
            }
            await ns.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<RtspRequestHead?> ReadRequestHeadAsync(NetworkStream ns, CancellationToken ct)
    {
        // RTSP request heads terminate with a blank line; these client requests have no body.
        var buffer = new List<byte>(256);
        byte[] one = new byte[1];
        while (!EndsWithDoubleCrlf(buffer))
        {
            int read = await ns.ReadAsync(one, ct);
            if (read == 0)
            {
                return null; // client closed
            }
            buffer.Add(one[0]);
        }

        string text = Encoding.ASCII.GetString(buffer.ToArray());
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] startLine = lines[0].Split(' ');

        var head = new RtspRequestHead { Method = startLine[0] };
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cseq))
            {
                head.CSeq = cseq;
            }
            else if (name.Equals("Transport", StringComparison.OrdinalIgnoreCase))
            {
                head.Transport = value;
            }
        }
        return head;
    }

    private static bool EndsWithDoubleCrlf(List<byte> buffer)
    {
        int n = buffer.Count;
        return n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n';
    }

    private sealed class RtspRequestHead
    {
        public required string Method { get; init; }
        public int CSeq { get; set; }
        public string? Transport { get; set; }
    }
}
