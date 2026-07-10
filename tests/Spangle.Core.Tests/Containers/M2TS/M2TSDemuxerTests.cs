using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Containers.M2TS;
using Spangle.Spinner;
using Spangle.Transport.SRT;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// Round-trip: frames muxed by <see cref="M2TSWriter"/> must come back out of
/// <see cref="M2TSDemuxer"/> + <see cref="M2TSMediaFrameAdapter{TContext}"/> as
/// canonical MediaFrames (avcC/ASC configs, length-prefixed samples, ms timestamps).
/// </summary>
public class M2TSDemuxerTests
{
    private static readonly byte[] s_sps = [0x67, 0x64, 0x00, 0x1F, 0xAA, 0xBB];
    private static readonly byte[] s_pps = [0x68, 0xEE, 0x3C, 0x80];
    private static readonly byte[] s_idr = [0x65, 0x11, 0x22, 0x33, 0x44];
    private static readonly byte[] s_p   = [0x41, 0x55, 0x66];
    private static readonly byte[] s_aac = [0xDE, 0xAD, 0xBE, 0xEF];

    [Fact]
    public async Task RoundTripThroughM2TSWriter()
    {
        // --- mux with the writer (video: key AU with in-band SPS/PPS, then a P AU; audio: one ADTS frame) ---
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264, HasAudio = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            AnnexB(s_sps, s_pps, s_idr), pts: 90_000 + 2_970, dts: 90_000, randomAccess: true, withPcr: true);
        muxer.WritePes(ts, M2TSWriter.PidAudio, M2TSWriter.StreamIdAudio,
            Adts(profile: 1, freqIndex: 3 /* 48kHz */, channels: 2, s_aac), pts: 90_000, dts: null,
            randomAccess: false, withPcr: false);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            AnnexB(s_p), pts: 93_600, dts: null, randomAccess: false, withPcr: false);

        // --- demux ---
        var dummy = new Pipe();
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        var context = new FakeReceiverContext(dummy.Reader, dummy.Writer) { MediaOutlet = media.Writer };

        var demuxer = new M2TSDemuxer();
        var adapter = new M2TSMediaFrameAdapter<FakeReceiverContext>(context);
        ReadOnlyMemory<byte> written = ts.WrittenMemory;
        for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
        {
            demuxer.ProcessPacket(written.Span.Slice(i, M2TSWriter.PacketSize), adapter);
        }
        demuxer.Flush(adapter);
        await media.Writer.CompleteAsync();

        context.VideoCodec.Should().Be(VideoCodec.H264, "the PMT names the video codec");
        context.AudioCodec.Should().Be(AudioCodec.AAC);

        // --- assert the canonical frames ---
        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(5);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Video);
        header.IsConfig.Should().BeTrue("the first video frame must be the avcC built from in-band SPS/PPS");
        header.Timestamp.Should().Be(1000u, "90kHz DTS 90000 is 1000 ms");
        payload.Should().Equal([
            0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1,
            0x00, (byte)s_sps.Length, .. s_sps,
            0x01,
            0x00, (byte)s_pps.Length, .. s_pps,
        ]);

        (header, payload) = frames[1];
        header.Kind.Should().Be(MediaFrameKind.Video);
        header.IsKeyFrame.Should().BeTrue();
        header.Timestamp.Should().Be(1000u);
        header.CompositionTimeMs.Should().Be(33, "PTS-DTS of 2970 ticks is 33 ms");
        payload.Should().Equal([0x00, 0x00, 0x00, (byte)s_idr.Length, .. s_idr],
            "SPS/PPS move into the config; the sample keeps the length-prefixed IDR only");

        // the second video AU and the audio PES are flushed at end of stream (video track first)
        (header, payload) = frames[2];
        header.Kind.Should().Be(MediaFrameKind.Video);
        header.IsKeyFrame.Should().BeFalse();
        header.Timestamp.Should().Be(1040u, "90kHz PTS 93600 is 1040 ms");
        header.CompositionTimeMs.Should().Be(0);
        payload.Should().Equal([0x00, 0x00, 0x00, (byte)s_p.Length, .. s_p]);

        (header, payload) = frames[3];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeTrue();
        payload.Should().Equal([0x11, 0x90], "AAC-LC 48kHz stereo AudioSpecificConfig");

        (header, payload) = frames[4];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeFalse();
        header.Timestamp.Should().Be(1000u);
        payload.Should().Equal(s_aac, "the ADTS header is stripped");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("live/test", "live/test")]
    [InlineData("#!::r=live/abc,m=publish", "live/abc")]
    [InlineData("#!::m=publish,r=live/abc", "live/abc")]
    [InlineData("#!::m=publish", null)]
    public void ParseStreamNameHandlesPlainAndAccessControlForms(string streamId, string? expected)
    {
        SRTReceiverContext.ParseStreamName(streamId).Should().Be(expected);
    }

    // =======================================================================

    private static async Task<List<(MediaFrameHeader Header, byte[] Payload)>> ReadAllFrames(PipeReader reader)
    {
        ReadResult result = await reader.ReadAsync();
        while (!result.IsCompleted)
        {
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            result = await reader.ReadAsync();
        }

        byte[] data = result.Buffer.ToArray();
        reader.AdvanceTo(result.Buffer.End);

        var frames = new List<(MediaFrameHeader, byte[])>();
        var pos = 0;
        while (pos < data.Length)
        {
            var header = MemoryMarshal.Read<MediaFrameHeader>(data.AsSpan(pos, MediaFrameHeader.Size));
            pos += MediaFrameHeader.Size;
            frames.Add((header, data.AsSpan(pos, header.Length).ToArray()));
            pos += header.Length;
        }
        return frames;
    }

    private static byte[] AnnexB(params byte[][] nalus)
    {
        var buff = new ArrayBufferWriter<byte>();
        foreach (byte[] nalu in nalus)
        {
            buff.Write<byte>([0x00, 0x00, 0x00, 0x01]);
            buff.Write(nalu.AsSpan());
        }
        return buff.WrittenSpan.ToArray();
    }

    private static byte[] Adts(byte profile, byte freqIndex, byte channels, byte[] payload)
    {
        int frameLength = 7 + payload.Length;
        var b = new byte[frameLength];
        b[0] = 0xFF;
        b[1] = 0xF1; // MPEG-4, layer 00, CRC absent
        b[2] = (byte)((profile << 6) | (freqIndex << 2) | ((channels >> 2) & 0x01));
        b[3] = (byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        b[4] = (byte)((frameLength >> 3) & 0xFF);
        b[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        b[6] = 0xFC;
        payload.CopyTo(b.AsSpan(7));
        return b;
    }

    private sealed class FakeReceiverContext(PipeReader reader, PipeWriter writer)
        : ReceiverContextBase<FakeReceiverContext>(reader, writer, CancellationToken.None)
    {
        public override string Id => "test";
        public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public override bool IsCompleted => false;
        public override ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource) =>
            ValueTask.CompletedTask;
    }
}
