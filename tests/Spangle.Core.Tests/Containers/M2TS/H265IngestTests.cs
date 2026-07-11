using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Spangle.Codecs.HEVC;
using Spangle.Containers.M2TS;
using Spangle.Spinner;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// H.265 over TS: the adapter must build a valid hvcC from the in-band VPS/SPS/PPS.
/// The SPS is composed bit-by-bit in the test, so the expected field values are exact.
/// </summary>
public class H265IngestTests
{
    private static readonly byte[] s_vps = [0x40, 0x01, 0x0C, 0x01, 0xFF, 0xFF];
    private static readonly byte[] s_pps = [0x44, 0x01, 0xC1, 0x72, 0xB4, 0x62, 0x40];
    private static readonly byte[] s_idr = [0x26, 0x01, 0xAF, 0x1D, 0x80]; // IDR_W_RADL (type 19)

    [Fact]
    public void HvcCBuilderExtractsTheSpsFields()
    {
        byte[] sps = BuildTestSps(width: 640, height: 360, levelIdc: 93);

        byte[] hvcc = HvcCBuilder.Build(s_vps, sps, s_pps, out HvcCBuilder.SpsSummary summary);

        summary.Width.Should().Be(640u);
        summary.Height.Should().Be(360u);
        summary.LevelIdc.Should().Be(93);
        summary.ChromaFormatIdc.Should().Be(1, "4:2:0");
        summary.ProfileSpaceTierIdc.Should().Be(0x01, "profile space 0, main tier, Main profile");
        summary.BitDepthLumaMinus8.Should().Be(0);

        hvcc[0].Should().Be(1, "configurationVersion");
        hvcc[12].Should().Be(93, "general_level_idc");
        (hvcc[16] & 0x03).Should().Be(1, "chromaFormat");
        (hvcc[21] & 0x03).Should().Be(3, "lengthSizeMinusOne = 3 (4-byte lengths)");
        hvcc[22].Should().Be(3, "one array each for VPS/SPS/PPS");

        // the read side of this repository must be able to consume what we build
        var parameterSets = new ArrayBufferWriter<byte>();
        int lengthSize = HEVCDecoderConfigurationRecord.Parse(new ReadOnlySequence<byte>(hvcc), parameterSets);
        lengthSize.Should().Be(4);
        byte[] annexB = parameterSets.WrittenSpan.ToArray();
        annexB.Should().ContainInConsecutiveOrder(s_vps);
        annexB.Should().ContainInConsecutiveOrder(sps);
        annexB.Should().ContainInConsecutiveOrder(s_pps);
    }

    [Fact]
    public async Task RoundTripH265ThroughM2TSWriter()
    {
        byte[] sps = BuildTestSps(width: 640, height: 360, levelIdc: 93);

        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H265 };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            AnnexB(s_vps, sps, s_pps, s_idr), pts: 90_000, dts: 90_000, randomAccess: true, withPcr: true);

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

        context.VideoCodec.Should().Be(VideoCodec.H265);
        context.VideoWidth.Should().Be(640u, "the adapter reports the dimensions from the SPS");
        context.VideoHeight.Should().Be(360u);

        var frames = await ReadAllFrames(media.Reader);
        frames.Should().HaveCount(2);

        (MediaFrameHeader header, byte[] payload) = frames[0];
        header.Kind.Should().Be(MediaFrameKind.Video);
        header.IsConfig.Should().BeTrue();
        header.VideoCodec.Should().Be(VideoCodec.H265);
        payload[0].Should().Be(1, "the config payload is the built hvcC");

        (header, payload) = frames[1];
        header.IsKeyFrame.Should().BeTrue("IDR_W_RADL is an IRAP");
        header.VideoCodec.Should().Be(VideoCodec.H265);
        payload.Should().Equal([0x00, 0x00, 0x00, (byte)s_idr.Length, .. s_idr],
            "parameter sets move into the hvcC; the sample keeps the length-prefixed IDR");
    }

    // =======================================================================

    [Fact]
    public void ConformanceWindowCropsTheDimensions()
    {
        // 1920x1088 coded size with a bottom crop of 4 (in 4:2:0 units of 2) = 1080 display
        byte[] sps = BuildTestSps(width: 1920, height: 1088, levelIdc: 123, cropBottom: 4);

        HvcCBuilder.Build(s_vps, sps, s_pps, out HvcCBuilder.SpsSummary summary);

        summary.Width.Should().Be(1920u);
        summary.Height.Should().Be(1080u, "the conformance window crops the padded coded size");
    }

    // =======================================================================

    /// <summary>Composes a minimal, spec-valid HEVC SPS (Main profile, 4:2:0, 8-bit).</summary>
    private static byte[] BuildTestSps(uint width, uint height, byte levelIdc, uint cropBottom = 0)
    {
        var w = new BitWriter();
        w.WriteBits(0, 4);  // sps_video_parameter_set_id
        w.WriteBits(0, 3);  // sps_max_sub_layers_minus1
        w.WriteBits(1, 1);  // sps_temporal_id_nesting_flag
        // profile_tier_level (general)
        w.WriteBits(0, 2);            // general_profile_space
        w.WriteBits(0, 1);            // general_tier_flag
        w.WriteBits(1, 5);            // general_profile_idc = Main
        w.WriteBits(0x60000000, 32);  // general_profile_compatibility_flags
        w.WriteBits(0x9000, 16);      // general_constraint_indicator_flags (48 bits)
        w.WriteBits(0, 32);
        w.WriteBits(levelIdc, 8);     // general_level_idc
        w.WriteUe(0);       // sps_seq_parameter_set_id
        w.WriteUe(1);       // chroma_format_idc = 4:2:0
        w.WriteUe(width);   // pic_width_in_luma_samples
        w.WriteUe(height);  // pic_height_in_luma_samples
        if (cropBottom > 0)
        {
            w.WriteBits(1, 1); // conformance_window_flag
            w.WriteUe(0);      // conf_win_left_offset
            w.WriteUe(0);      // conf_win_right_offset
            w.WriteUe(0);      // conf_win_top_offset
            w.WriteUe(cropBottom);
        }
        else
        {
            w.WriteBits(0, 1); // conformance_window_flag
        }
        w.WriteUe(0);       // bit_depth_luma_minus8
        w.WriteUe(0);       // bit_depth_chroma_minus8
        w.WriteBits(1, 1);  // rbsp_stop_one_bit (the parser reads no further)

        byte[] rbsp = w.ToArray();
        var sps = new byte[2 + rbsp.Length];
        sps[0] = 33 << 1; // NAL header: SPS
        sps[1] = 0x01;
        rbsp.CopyTo(sps, 2);
        return sps;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bitPos;

        public void WriteBits(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if ((_bitPos & 7) == 0)
                {
                    _bytes.Add(0);
                }
                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (7 - (_bitPos & 7)));
                }
                _bitPos++;
            }
        }

        public void WriteUe(uint value)
        {
            int leadingZeros = 63 - System.Numerics.BitOperations.LeadingZeroCount(value + 1UL);
            WriteBits(0, leadingZeros);
            WriteBits(value + 1UL, leadingZeros + 1);
        }

        public byte[] ToArray() => _bytes.ToArray();
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
