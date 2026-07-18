using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Codecs.Opus;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// Opus over TS: private PES (stream_type 0x06) identified by its registration
/// descriptor, access units framed by control headers. The adapter synthesizes an
/// OpusHead config and emits raw Opus packets with TOC-derived timestamps.
/// </summary>
public class OpusIngestTests
{
    private static readonly byte[] s_au1 = [0xF8, 0x11, 0x22, 0x33]; // CELT FB 20ms
    private static readonly byte[] s_au2 = [0xF8, 0x44, 0x55];

    [Fact]
    public void OpusProgramIsMappedFromDescriptors()
    {
        var sink = new RecordingSink();
        var demuxer = new M2TSDemuxer();
        demuxer.ProcessPacket(WriterPatPacket(), sink);
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: true, cc: 0, PmtPointerAndSection()), sink);

        sink.AudioStreamType.Should().Be(M2TSStreamType.PrivatePes);
        sink.OpusChannels.Should().Be(2);
    }

    [Fact]
    public async Task OpusPesRoundTripsToCanonicalFrames()
    {
        var dummy = new Pipe();
        var media = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        var context = new FakeReceiverContext(dummy.Reader, dummy.Writer) { MediaOutlet = media.Writer };

        var demuxer = new M2TSDemuxer();
        var adapter = new M2TSMediaFrameAdapter<FakeReceiverContext>(context);
        demuxer.ProcessPacket(WriterPatPacket(), adapter);
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: true, cc: 0, PmtPointerAndSection()), adapter);
        demuxer.ProcessPacket(TsPacket(0x0101, pusi: true, cc: 0, OpusPes(pts90k: 90_000)), adapter);
        demuxer.Flush(adapter);
        await media.Writer.CompleteAsync();

        context.IsAudioOnly.Should().BeTrue();
        context.AudioCodec.Should().Be(AudioCodec.Opus);

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(3);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Audio);
        header.IsConfig.Should().BeTrue();
        header.AudioCodec.Should().Be(AudioCodec.Opus);
        OpusPacket.IsOpusHead(payload).Should().BeTrue("the synthesized config is an OpusHead");
        OpusPacket.ParseOpusHead(payload).ChannelCount.Should().Be(2);

        (header, payload) = frames[1];
        header.IsConfig.Should().BeFalse();
        header.Timestamp.Should().Be(90000L);
        payload.Should().Equal(s_au1);

        (header, payload) = frames[2];
        header.Timestamp.Should().Be(91800L, "the first AU is 960 samples = 20 ms = 1800 ticks");
        payload.Should().Equal(s_au2);
    }

    // =======================================================================

    private static byte[] WriterPatPacket()
    {
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264, HasAudio = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        return ts.WrittenSpan[..M2TSWriter.PacketSize].ToArray();
    }

    /// <summary>PMT with a single ES: stream_type 0x06 + registration "Opus" + extension (stereo).</summary>
    private static byte[] PmtPointerAndSection()
    {
        byte[] descriptors =
        [
            0x05, 0x04, (byte)'O', (byte)'p', (byte)'u', (byte)'s', // registration
            0x7F, 0x02, 0x80, 0x02,                                 // extension: channel_config_code = stereo
        ];
        int sectionLength = 9 + 5 + descriptors.Length + 4;
        var s = new byte[1 + 3 + sectionLength]; // pointer_field + section
        s[0] = 0x00; // pointer_field
        s[1] = 0x02; // table_id: PMT
        s[2] = (byte)(0xB0 | (sectionLength >> 8));
        s[3] = (byte)sectionLength;
        s[4] = 0x00; s[5] = 0x01; // program_number
        s[6] = 0xC3;              // version 1 + current_next
        s[7] = 0x00;
        s[8] = 0x00;
        s[9] = 0xE1; s[10] = 0x01; // PCR PID = 0x0101
        s[11] = 0xF0; s[12] = 0x00; // program_info_length = 0
        s[13] = M2TSStreamType.PrivatePes;
        s[14] = 0xE1; s[15] = 0x01; // ES PID 0x0101
        s[16] = (byte)(0xF0 | (descriptors.Length >> 8));
        s[17] = (byte)descriptors.Length;
        descriptors.CopyTo(s, 18);
        // CRC32 left zero (not verified)
        return s;
    }

    /// <summary>One PES packet holding two control-header-framed Opus AUs.</summary>
    private static byte[] OpusPes(ulong pts90k)
    {
        var es = new List<byte>();
        foreach (byte[] au in (byte[][])[s_au1, s_au2])
        {
            es.Add(0x7F);
            es.Add(0xE0);
            es.Add((byte)au.Length);
            es.AddRange(au);
        }

        var pes = new List<byte> { 0x00, 0x00, 0x01, 0xBD }; // private_stream_1
        int packetLength = 3 + 5 + es.Count;
        pes.Add((byte)(packetLength >> 8));
        pes.Add((byte)packetLength);
        pes.Add(0x84);              // marker + data_alignment
        pes.Add(0x80);              // PTS only
        pes.Add(0x05);              // header data length
        pes.Add((byte)(0x21 | (byte)((pts90k >> 29) & 0x0E)));
        pes.Add((byte)(pts90k >> 22));
        pes.Add((byte)(0x01 | (pts90k >> 14)));
        pes.Add((byte)(pts90k >> 7));
        pes.Add((byte)(0x01 | (pts90k << 1)));
        pes.AddRange(es);
        return pes.ToArray();
    }

    private static byte[] TsPacket(ushort pid, bool pusi, byte cc, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[M2TSWriter.PacketSize];
        pkt[0] = 0x47;
        pkt[1] = (byte)((pusi ? 0x40 : 0x00) | (pid >> 8));
        pkt[2] = unchecked((byte)pid);
        pkt[3] = (byte)(0x10 | (cc & 0x0F));
        payload.CopyTo(pkt.AsSpan(4));
        pkt.AsSpan(4 + payload.Length).Fill(0xFF);
        return pkt;
    }

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

    private sealed class RecordingSink : IM2TSDemuxerSink
    {
        public byte AudioStreamType { get; private set; }
        public byte OpusChannels { get; private set; }

        public void OnProgramMapped(byte videoStreamType, ushort videoPid, byte audioStreamType, ushort audioPid,
            byte opusChannels)
        {
            AudioStreamType = audioStreamType;
            OpusChannels = opusChannels;
        }

        public void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
        {
        }
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
