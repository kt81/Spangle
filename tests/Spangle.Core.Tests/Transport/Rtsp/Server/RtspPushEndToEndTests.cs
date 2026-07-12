using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Spangle.Transport.HLS;
using Spangle.Transport.Rtsp.Server;

namespace Spangle.Tests.Transport.Rtsp.Server;

/// <summary>
/// A full RTSP <em>push</em> ingest driven against the real
/// <see cref="RtspPushReceiverContext"/>: a scripted client ANNOUNCE/SETUP/RECORDs an
/// H.264 stream over TCP-interleaved transport and then pushes RTP, and the
/// <see cref="LiveContext"/> republishes it as HLS. It proves the whole publish path is
/// connected end to end — the handshake is answered, interleaved RTP is depacketized into
/// canonical MediaFrames, and a playlist plus at least one media segment appear in
/// storage. Wired exactly like Spangle.Extensions.Kestrel/RtspConnectionHandler.cs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks complete before disposal",
    Justification = "The push, live, sender and drain tasks all read/write the pipes, storage and sender; " +
        "the finally block awaits every one of them to completion before those objects are disposed, which " +
        "the analyzer cannot prove across the loops.")]
public class RtspPushEndToEndTests
{
    private const string StreamName = "testcam";
    private const byte VideoPayloadType = 96;
    private const int TimestampStep = 3000; // 90 kHz / 30 fps
    private const int KeyFrameInterval = 15;

    // A minimal but valid H.264 baseline SPS/PPS (TS output does not need the dimensions),
    // plus arbitrary IDR/non-IDR slice bodies. NAL headers: F=0, NRI=3.
    private static readonly byte[] s_sps = [0x67, 0x42, 0x00, 0x0A, 0xF8, 0x41, 0xA2];
    private static readonly byte[] s_pps = [0x68, 0xCE, 0x38, 0x80];
    private static readonly byte[] s_idr = [0x65, 0x88, 0x84, 0x21, 0x3F, 0x20, 0x10];
    private static readonly byte[] s_pframe = [0x61, 0x9A, 0x00, 0x10, 0x20, 0x30];

    [Fact]
    public async Task AcceptsRtspPushAndProducesHlsOutput()
    {
        // A hard budget so a bug in the ingest path can never hang CI.
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct = hardTimeout.Token;

        // toServer: the client writes RTSP requests + interleaved RTP; the receiver reads it.
        // fromServer: the receiver writes its responses; the test just drains them.
        var toServer = new Pipe();
        var fromServer = new Pipe();

        var receiver = new RtspPushReceiverContext(toServer.Reader, fromServer.Writer,
            new IPEndPoint(IPAddress.Loopback, 0), ct);
        var storage = new MemoryHLSStorage();
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
        Task drainTask = DrainAsync(fromServer.Reader, ct);
        Task pushTask = PushClientAsync(toServer.Writer, ct);

        try
        {
            // Wait for a media segment to appear in the live playlist.
            string? segmentName = await PollForSegmentAsync(storage, ct);
            segmentName.Should().NotBeNull("the RTSP push must republish at least one HLS media segment");

            storage.TryGetStream(StreamName, out IHLSStreamStorage streamStore).Should().BeTrue();
            streamStore.Playlist.Should().Contain("#EXTM3U", "a valid HLS media playlist was published");
            streamStore.TryReadBlob(segmentName!, out ReadOnlyMemory<byte> segment).Should().BeTrue();
            segment.Length.Should().BeGreaterThan(0, "the segment blob must hold muxed TS data");
        }
        finally
        {
            // Cancel the shared budget and close the client side; the receiver's read loop
            // ends and the ingest drains cleanly. Every task shares the 15s hard-timeout
            // token, so awaiting these to completion is bounded and cannot hang; the
            // background tasks are drained before the objects they use are disposed.
            await hardTimeout.CancelAsync();
            await toServer.Writer.CompleteAsync();
            try { await pushTask; } catch (Exception) { /* cancelled / writer completed */ }
            try { await liveTask; } catch (Exception) { /* cancelled transport */ }
            try { await senderTask; } catch (Exception) { /* intake completed on shutdown */ }
            try { await drainTask; } catch (Exception) { /* cancelled reader */ }
            sender.Dispose();
            live.Dispose();
        }
    }

    // =======================================================================
    // client side: the RTSP push

    private static async Task PushClientAsync(PipeWriter writer, CancellationToken ct)
    {
        try
        {
            string sdp = BuildSdp();
            await WriteTextAsync(writer,
                "OPTIONS rtsp://127.0.0.1/live/testcam RTSP/1.0\r\nCSeq: 1\r\n\r\n", ct);
            await WriteTextAsync(writer,
                "ANNOUNCE rtsp://127.0.0.1/live/testcam RTSP/1.0\r\nCSeq: 2\r\n" +
                "Content-Type: application/sdp\r\n" +
                $"Content-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n{sdp}", ct);
            await WriteTextAsync(writer,
                "SETUP rtsp://127.0.0.1/live/testcam/streamid=0 RTSP/1.0\r\nCSeq: 3\r\n" +
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1;mode=record\r\n\r\n", ct);
            await WriteTextAsync(writer,
                "RECORD rtsp://127.0.0.1/live/testcam RTSP/1.0\r\nCSeq: 4\r\n\r\n", ct);

            ushort seq = 0;
            uint ts = 0;
            for (int frameIndex = 0; !ct.IsCancellationRequested; frameIndex++)
            {
                if (frameIndex % KeyFrameInterval == 0)
                {
                    // An IDR access unit: SPS + PPS + IDR share a timestamp; the marker on
                    // the IDR closes the unit.
                    await WriteNalAsync(writer, seq++, ts, marker: false, s_sps, ct);
                    await WriteNalAsync(writer, seq++, ts, marker: false, s_pps, ct);
                    await WriteNalAsync(writer, seq++, ts, marker: true, s_idr, ct);
                }
                else
                {
                    await WriteNalAsync(writer, seq++, ts, marker: true, s_pframe, ct);
                }

                ts += TimestampStep;
                await Task.Delay(5, ct); // ~real time so segments cut on wall-clock too
            }
        }
        catch (OperationCanceledException)
        {
            // deliberate shutdown once a segment was observed
        }
    }

    private static async ValueTask WriteTextAsync(PipeWriter writer, string text, CancellationToken ct)
    {
        await writer.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
    }

    private static async ValueTask WriteNalAsync(PipeWriter writer, ushort seq, uint ts, bool marker,
        byte[] nal, CancellationToken ct)
    {
        byte[] rtp = BuildRtpPacket(VideoPayloadType, seq, ts, marker, nal);
        await writer.WriteAsync(Interleave(channel: 0, rtp), ct);
    }

    private static string BuildSdp()
    {
        string sprop = $"{Convert.ToBase64String(s_sps)},{Convert.ToBase64String(s_pps)}";
        return "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=Spangle Push Test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n" +
            "a=control:*\r\n" +
            "m=video 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={sprop}\r\n" +
            "a=control:streamid=0\r\n";
    }

    private static byte[] BuildRtpPacket(byte payloadType, ushort seq, uint timestamp, bool marker,
        ReadOnlySpan<byte> payload)
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

    // =======================================================================
    // test-side polling and drain

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

    private static async Task DrainAsync(PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // deliberate shutdown
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}
