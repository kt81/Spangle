using System.Buffers;
using System.Text;
using FluentAssertions;
using Spangle.Containers.M2TS;

namespace Spangle.Tests.Containers.M2TS;

public class Crc32Mpeg2Tests
{
    [Fact]
    public void ComputeKnownVector()
    {
        // CRC-32/MPEG-2 of ASCII "123456789"
        uint crc = Crc32Mpeg2.Compute(Encoding.ASCII.GetBytes("123456789"));
        crc.Should().Be(0x0376E6E7u);
    }
}

public class M2TSWriterTests
{
    private const int PacketSize = M2TSWriter.PacketSize;

    [Fact]
    public void ProgramTablesAreWellFormed()
    {
        var writer = new M2TSWriter();
        var outlet = new ArrayBufferWriter<byte>();
        writer.WriteProgramTables(outlet);

        outlet.WrittenCount.Should().Be(PacketSize * 2);
        var pat = outlet.WrittenSpan[..PacketSize];
        var pmt = outlet.WrittenSpan[PacketSize..];

        // PAT
        pat[0].Should().Be(0x47);
        GetPid(pat).Should().Be(M2TSWriter.PidPat);
        HasPayloadUnitStart(pat).Should().BeTrue();
        pat[4].Should().Be(0x00, "pointer_field");
        var patSection = pat.Slice(5, 16);
        patSection[0].Should().Be(0x00, "table_id");
        // program_number = 1 -> PMT PID
        ((patSection[8] << 8) | patSection[9]).Should().Be(1);
        ((ushort)(((patSection[10] & 0x1F) << 8) | patSection[11])).Should().Be(M2TSWriter.PidPmt);
        VerifySectionCrc(patSection);

        // PMT (video only)
        pmt[0].Should().Be(0x47);
        GetPid(pmt).Should().Be(M2TSWriter.PidPmt);
        var pmtSection = pmt.Slice(5, 3 + 18);
        pmtSection[0].Should().Be(0x02, "table_id");
        // PCR PID
        ((ushort)(((pmtSection[8] & 0x1F) << 8) | pmtSection[9])).Should().Be(M2TSWriter.PidVideo);
        // Single H.264 stream
        pmtSection[12].Should().Be(0x1B, "stream_type H.264");
        ((ushort)(((pmtSection[13] & 0x1F) << 8) | pmtSection[14])).Should().Be(M2TSWriter.PidVideo);
        VerifySectionCrc(pmtSection);
    }

    [Fact]
    public void ProgramTablesIncludeAudioWhenEnabled()
    {
        var writer = new M2TSWriter { HasAudio = true };
        var outlet = new ArrayBufferWriter<byte>();
        writer.WriteProgramTables(outlet);

        var pmt = outlet.WrittenSpan[PacketSize..];
        int sectionLength = ((pmt[6] & 0x0F) << 8) | pmt[7]; // after table_id at offset 5
        sectionLength.Should().Be(9 + 5 * 2 + 4);
        var pmtSection = pmt.Slice(5, 3 + sectionLength);
        pmtSection[12].Should().Be(0x1B, "stream_type H.264");
        pmtSection[17].Should().Be(0x0F, "stream_type ADTS AAC");
        ((ushort)(((pmtSection[18] & 0x1F) << 8) | pmtSection[19])).Should().Be(M2TSWriter.PidAudio);
        VerifySectionCrc(pmtSection);
    }

    [Fact]
    public void SmallPesFitsInOnePacketWithStuffing()
    {
        var writer = new M2TSWriter();
        var outlet = new ArrayBufferWriter<byte>();
        byte[] payload = Enumerable.Range(0, 100).Select(static i => (byte)i).ToArray();
        const ulong pts = 12345678;
        const ulong dts = 12340000;

        writer.WritePes(outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            payload, pts, dts, randomAccess: true, withPcr: true);

        outlet.WrittenCount.Should().Be(PacketSize);
        var pkt = outlet.WrittenSpan;

        pkt[0].Should().Be(0x47);
        GetPid(pkt).Should().Be(M2TSWriter.PidVideo);
        HasPayloadUnitStart(pkt).Should().BeTrue();
        (pkt[3] >> 4).Should().Be(0x3, "adaptation field + payload");

        int afLen = pkt[4] + 1;
        ((pkt[5] & 0x40) != 0).Should().BeTrue("random_access_indicator");
        ((pkt[5] & 0x10) != 0).Should().BeTrue("PCR_flag");
        DecodePcrBase(pkt.Slice(6, 6)).Should().Be(dts);

        // PES header follows the adaptation field
        var pes = pkt[(4 + afLen)..];
        pes[0].Should().Be(0x00);
        pes[1].Should().Be(0x00);
        pes[2].Should().Be(0x01);
        pes[3].Should().Be(M2TSWriter.StreamIdVideo);
        pes[7].Should().Be(0xC0, "PTS and DTS present");
        pes[8].Should().Be(10, "PES header data length");
        DecodeTimestamp(pes.Slice(9, 5)).Should().Be(pts);
        DecodeTimestamp(pes.Slice(14, 5)).Should().Be(dts);
        pes[19..].ToArray().Should().Equal(payload);
    }

    [Fact]
    public void LargePesIsSplitWithContinuityCounters()
    {
        var writer = new M2TSWriter();
        var outlet = new ArrayBufferWriter<byte>();
        byte[] payload = Enumerable.Range(0, 400).Select(static i => (byte)(i & 0xFF)).ToArray();
        const ulong pts = 90000;

        writer.WritePes(outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            payload, pts, null, randomAccess: false, withPcr: true);

        (outlet.WrittenCount % PacketSize).Should().Be(0);
        int packets = outlet.WrittenCount / PacketSize;
        packets.Should().Be(3);

        var reassembled = new List<byte>();
        for (var i = 0; i < packets; i++)
        {
            var pkt = outlet.WrittenSpan.Slice(i * PacketSize, PacketSize);
            pkt[0].Should().Be(0x47);
            GetPid(pkt).Should().Be(M2TSWriter.PidVideo);
            HasPayloadUnitStart(pkt).Should().Be(i == 0, "only the first packet starts the payload unit");
            (pkt[3] & 0x0F).Should().Be(i & 0x0F, "continuity counter increments");

            var hasAf = (pkt[3] & 0x20) != 0;
            int headerLen = 4 + (hasAf ? pkt[4] + 1 : 0);
            if (i == 1)
            {
                hasAf.Should().BeFalse("middle packets are full payload");
            }
            reassembled.AddRange(pkt[headerLen..].ToArray());
        }

        // PES header (9 + 5 for PTS only) + payload
        var pesHeaderLen = 14;
        reassembled.Count.Should().Be(pesHeaderLen + payload.Length);
        reassembled.Skip(pesHeaderLen).Should().Equal(payload);
        DecodeTimestamp(reassembled.Skip(9).Take(5).ToArray()).Should().Be(pts);
    }

    [Fact]
    public void ContinuityCountersAreKeptAcrossCalls()
    {
        var writer = new M2TSWriter();
        var outlet = new ArrayBufferWriter<byte>();
        byte[] payload = [0x01, 0x02, 0x03];

        writer.WritePes(outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, payload, 0, null, false, false);
        writer.WritePes(outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, payload, 3000, null, false, false);

        var first = outlet.WrittenSpan[..PacketSize];
        var second = outlet.WrittenSpan[PacketSize..];
        (first[3] & 0x0F).Should().Be(0);
        (second[3] & 0x0F).Should().Be(1);
    }

    private static ushort GetPid(ReadOnlySpan<byte> packet)
        => (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);

    private static bool HasPayloadUnitStart(ReadOnlySpan<byte> packet)
        => (packet[1] & 0x40) != 0;

    private static void VerifySectionCrc(ReadOnlySpan<byte> section)
    {
        uint stored = ((uint)section[^4] << 24) | ((uint)section[^3] << 16) | ((uint)section[^2] << 8) | section[^1];
        Crc32Mpeg2.Compute(section[..^4]).Should().Be(stored, "PSI section CRC must match");
    }

    private static ulong DecodeTimestamp(ReadOnlySpan<byte> b)
        => ((ulong)((b[0] >> 1) & 0x07) << 30)
           | ((ulong)b[1] << 22)
           | ((ulong)(b[2] >> 1) << 15)
           | ((ulong)b[3] << 7)
           | ((ulong)b[4] >> 1);

    private static ulong DecodePcrBase(ReadOnlySpan<byte> b)
        => ((ulong)b[0] << 25)
           | ((ulong)b[1] << 17)
           | ((ulong)b[2] << 9)
           | ((ulong)b[3] << 1)
           | ((ulong)b[4] >> 7);
}
