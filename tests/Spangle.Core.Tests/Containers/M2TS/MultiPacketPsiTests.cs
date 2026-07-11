using System.Buffers;
using Spangle.Containers.M2TS;

namespace Spangle.Tests.Containers.M2TS;

/// <summary>
/// PSI sections larger than one TS packet payload (184 bytes) must be reassembled
/// across packets — real muxers emit such PMTs when descriptors pile up.
/// </summary>
public class MultiPacketPsiTests
{
    [Fact]
    public void PmtSpanningTwoPacketsIsReassembled()
    {
        var sink = new RecordingSink();
        var demuxer = new M2TSDemuxer();

        demuxer.ProcessPacket(WriterPatPacket(), sink);

        byte[] section = BuildPmtSection(programInfoPadding: 200);
        section.Length.Should().BeGreaterThan(184, "the test must actually span two packets");

        // packet 1: pointer_field 0 + the first 183 section bytes
        var first = new byte[184];
        first[0] = 0;
        section.AsSpan(0, 183).CopyTo(first.AsSpan(1));
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: true, cc: 0, first), sink);

        sink.ProgramMappings.Should().BeEmpty("the section is still incomplete");

        // packet 2: continuation with the remaining bytes
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: false, cc: 1, section.AsSpan(183)), sink);

        sink.ProgramMappings.Should().ContainSingle()
            .Which.Should().Be(((byte)0x1B, (byte)0x0F), "H.264 + ADTS AAC from the spanning PMT");
    }

    [Fact]
    public void BrokenContinuityDropsThePartialSection()
    {
        var sink = new RecordingSink();
        var demuxer = new M2TSDemuxer();

        demuxer.ProcessPacket(WriterPatPacket(), sink);

        byte[] section = BuildPmtSection(programInfoPadding: 200);
        var first = new byte[184];
        first[0] = 0;
        section.AsSpan(0, 183).CopyTo(first.AsSpan(1));
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: true, cc: 0, first), sink);

        // the continuation packet is lost; a later one arrives with a gap in the counter
        demuxer.ProcessPacket(TsPacket(M2TSWriter.PidPmt, pusi: false, cc: 3, section.AsSpan(183)), sink);

        sink.ProgramMappings.Should().BeEmpty("a partial section with a packet hole must not be parsed");
    }

    // =======================================================================

    /// <summary>The PAT as the writer emits it (PMT PID 0x1000); first packet of the program tables.</summary>
    private static byte[] WriterPatPacket()
    {
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264, HasAudio = true };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        return ts.WrittenSpan[..M2TSWriter.PacketSize].ToArray();
    }

    /// <summary>
    /// A valid PMT section (H.264 on 0x0100, AAC on 0x0101) inflated past one packet
    /// payload with a private descriptor in the program info loop.
    /// </summary>
    private static byte[] BuildPmtSection(int programInfoPadding)
    {
        int programInfoLength = 2 + programInfoPadding; // descriptor tag + length + body
        int sectionLength = 9 + programInfoLength + 2 * 5 + 4;
        var s = new byte[3 + sectionLength];

        s[0] = 0x02; // table_id: PMT
        s[1] = (byte)(0xB0 | (sectionLength >> 8));
        s[2] = (byte)sectionLength;
        s[3] = 0x00; // program_number = 1
        s[4] = 0x01;
        s[5] = 0xC3; // reserved + version 1 + current_next
        s[6] = 0x00; // section_number
        s[7] = 0x00; // last_section_number
        s[8] = 0xE0 | (M2TSWriter.PidVideo >> 8); // PCR PID
        s[9] = unchecked((byte)M2TSWriter.PidVideo);
        s[10] = (byte)(0xF0 | (programInfoLength >> 8));
        s[11] = (byte)programInfoLength;

        int pos = 12;
        s[pos++] = 0xC0; // user-private descriptor as padding
        s[pos++] = (byte)programInfoPadding;
        pos += programInfoPadding; // body stays zero

        pos = WriteEsEntry(s, pos, 0x1B, M2TSWriter.PidVideo);
        pos = WriteEsEntry(s, pos, 0x0F, M2TSWriter.PidAudio);

        // CRC32 is not verified by the demuxer; leave it zero
        (pos + 4).Should().Be(s.Length);
        return s;
    }

    private static int WriteEsEntry(byte[] s, int pos, byte streamType, ushort pid)
    {
        s[pos] = streamType;
        s[pos + 1] = (byte)(0xE0 | (pid >> 8));
        s[pos + 2] = unchecked((byte)pid);
        s[pos + 3] = 0xF0; // es_info_length = 0
        s[pos + 4] = 0x00;
        return pos + 5;
    }

    private static byte[] TsPacket(ushort pid, bool pusi, byte cc, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[M2TSWriter.PacketSize];
        pkt[0] = 0x47;
        pkt[1] = (byte)((pusi ? 0x40 : 0x00) | (pid >> 8));
        pkt[2] = unchecked((byte)pid);
        pkt[3] = (byte)(0x10 | (cc & 0x0F)); // payload only
        payload.CopyTo(pkt.AsSpan(4));
        pkt.AsSpan(4 + payload.Length).Fill(0xFF); // stuffing
        return pkt;
    }

    private sealed class RecordingSink : IM2TSDemuxerSink
    {
        public List<(byte Video, byte Audio)> ProgramMappings { get; } = [];

        public void OnProgramMapped(byte videoStreamType, ushort videoPid, byte audioStreamType, ushort audioPid,
            byte opusChannels) =>
            ProgramMappings.Add((videoStreamType, audioStreamType));

        public void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es)
        {
        }
    }
}
