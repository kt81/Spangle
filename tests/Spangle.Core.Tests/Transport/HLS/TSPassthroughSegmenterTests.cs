using System.Buffers;
using Spangle.Containers.M2TS;
using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

/// <summary>
/// The passthrough segmenter re-segments a foreign TS at random-access video PES
/// starts, injecting the cached PAT/PMT at every segment head so each segment is
/// independently decodable even though the source only sent its tables once.
/// </summary>
public class TSPassthroughSegmenterTests
{
    private static readonly byte[] s_keyAu = [0x00, 0x00, 0x00, 0x01, 0x65, 0x11, 0x22, 0x33];
    private static readonly byte[] s_pAu   = [0x00, 0x00, 0x00, 0x01, 0x41, 0x44, 0x55];

    [Fact]
    public void CutsAtRandomAccessPointsAndInjectsTables()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");

        // A "foreign" TS: tables once at the head only, keyframes 2.5s apart
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
            pts: 0, dts: null, randomAccess: true, withPcr: true);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_pAu,
            pts: 90_000, dts: null, randomAccess: false, withPcr: true);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
            pts: 225_000, dts: null, randomAccess: true, withPcr: true);

        var segmenter = new TSPassthroughSegmenter(storage, targetDuration: 2.0);
        ReadOnlySpan<byte> written = ts.WrittenSpan;
        for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
        {
            segmenter.ProcessPacket(written.Slice(i, M2TSWriter.PacketSize));
        }
        segmenter.Complete();

        string playlist = storage.Playlist!;
        playlist.Should().Contain("#EXTINF:2.500,\nseg00000.ts", "the second keyframe closes the first segment");
        playlist.Should().Contain("seg00001.ts");
        playlist.Should().Contain("#EXT-X-ENDLIST");

        // Both segments must start with PAT + PMT + a random-access video packet
        var segments = new List<byte[]>();
        foreach (string name in (string[])["seg00000.ts", "seg00001.ts"])
        {
            storage.TryReadBlob(name, out ReadOnlyMemory<byte> segMemory).Should().BeTrue();
            byte[] seg = segMemory.ToArray();
            segments.Add(seg);
            (seg.Length % M2TSWriter.PacketSize).Should().Be(0);
            PidOf(seg, 0).Should().Be(0x0000, $"{name} must start with the injected PAT");
            PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt, $"{name} continues with the injected PMT");
            PidOf(seg, 2).Should().Be(M2TSWriter.PidVideo);
            (seg[2 * M2TSWriter.PacketSize + 1] & 0x40).Should().NotBe(0, "the video packet starts a PES");
        }

        // Injected PSI continuity counters must be gapless across segments
        byte patCc0 = (byte)(segments[0][3] & 0x0F);
        byte patCc1 = (byte)(segments[1][3] & 0x0F);
        patCc1.Should().Be((byte)((patCc0 + 1) & 0x0F));
    }

    [Fact]
    public void DropsTheHeadUntilTheFirstRandomAccessPoint()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");

        // The stream joins mid-GOP: a non-key AU comes before the first keyframe
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_pAu,
            pts: 0, dts: null, randomAccess: false, withPcr: true);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
            pts: 90_000, dts: null, randomAccess: true, withPcr: true);

        var segmenter = new TSPassthroughSegmenter(storage, targetDuration: 2.0);
        ReadOnlySpan<byte> written = ts.WrittenSpan;
        for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
        {
            segmenter.ProcessPacket(written.Slice(i, M2TSWriter.PacketSize));
        }
        segmenter.Complete();

        storage.TryReadBlob("seg00000.ts", out ReadOnlyMemory<byte> segMemory).Should().BeTrue();
        byte[] seg = segMemory.ToArray();
        PidOf(seg, 0).Should().Be(0x0000);
        PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt);
        // the mid-GOP P-frame must not be in the segment: 2 PSI + the keyframe PES only
        int videoPackets = Enumerable.Range(2, seg.Length / M2TSWriter.PacketSize - 2)
            .Count(i => PidOf(seg, i) == M2TSWriter.PidVideo);
        byte[] source = ts.WrittenSpan.ToArray();
        int keyframePackets = CountPackets(source, from: 2 + PacketsOf(source, 2)); // packets of the second PES
        videoPackets.Should().Be(keyframePackets, "only the keyframe access unit is kept");
    }

    [Fact]
    public void ReassemblesAndReinjectsAMultiPacketPmt()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");

        // PAT (declares the PMT PID) from the muxer, then a PMT too large for one TS packet, then a
        // random-access video PES. A foreign source repeats its tables once; the segment must carry
        // the whole PMT — both packets — or a large program becomes undecodable at the segment head.
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var head = new ArrayBufferWriter<byte>();
        muxer.WriteProgramTables(head);
        byte[] pat = head.WrittenSpan[..M2TSWriter.PacketSize].ToArray(); // packet 0 is the PAT

        (byte[] pmtA, byte[] pmtB) = BuildTwoPacketPmt();

        var pes = new ArrayBufferWriter<byte>();
        muxer.WritePes(pes, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
            pts: 0, dts: null, randomAccess: true, withPcr: true);

        var segmenter = new TSPassthroughSegmenter(storage, targetDuration: 2.0);
        segmenter.ProcessPacket(pat);
        segmenter.ProcessPacket(pmtA);
        segmenter.ProcessPacket(pmtB);
        ReadOnlySpan<byte> pesPackets = pes.WrittenSpan;
        for (var i = 0; i < pesPackets.Length; i += M2TSWriter.PacketSize)
        {
            segmenter.ProcessPacket(pesPackets.Slice(i, M2TSWriter.PacketSize));
        }
        segmenter.Complete();

        storage.TryReadBlob("seg00000.ts", out ReadOnlyMemory<byte> segMemory).Should().BeTrue();
        byte[] seg = segMemory.ToArray();

        PidOf(seg, 0).Should().Be(M2TSWriter.PidPat, "the segment starts with the injected PAT");
        PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt, "then the first PMT packet");
        PidOf(seg, 2).Should().Be(M2TSWriter.PidPmt, "and the second PMT packet (the section spans two)");
        PidOf(seg, 3).Should().Be(M2TSWriter.PidVideo, "then the random-access video PES");

        // Both PMT packets are re-injected verbatim apart from the continuity counter (byte 3).
        seg.AsSpan(1 * M2TSWriter.PacketSize + 4, M2TSWriter.PacketSize - 4).ToArray()
            .Should().Equal(pmtA.AsSpan(4).ToArray(), "PMT packet 1 payload is preserved");
        seg.AsSpan(2 * M2TSWriter.PacketSize + 4, M2TSWriter.PacketSize - 4).ToArray()
            .Should().Equal(pmtB.AsSpan(4).ToArray(), "PMT packet 2 payload is preserved");
    }

    // =======================================================================

    /// <summary>A valid PMT section (H.264 on the video PID) inflated with a large program-info
    /// block so it spans two TS packets, split onto the PMT PID.</summary>
    private static (byte[] PacketA, byte[] PacketB) BuildTwoPacketPmt()
    {
        const int programInfoLength = 170;         // pushes the section past one packet's payload
        const int sectionLength = 18 + programInfoLength; // program_number .. CRC (ISO 13818-1 2.4.4.9)
        var section = new byte[3 + sectionLength];
        section[0] = 0x02;                                          // table_id = PMT
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));  // syntax indicator + length hi
        section[2] = (byte)(sectionLength & 0xFF);
        section[3] = 0x00; section[4] = 0x01;                       // program_number = 1
        section[5] = 0xC1;                                          // version 0, current_next = 1
        section[6] = 0x00;                                          // section_number
        section[7] = 0x00;                                          // last_section_number
        section[8] = (byte)(0xE0 | (M2TSWriter.PidVideo >> 8));     // PCR_PID
        section[9] = unchecked((byte)M2TSWriter.PidVideo);
        section[10] = (byte)(0xF0 | ((programInfoLength >> 8) & 0x0F));
        section[11] = (byte)(programInfoLength & 0xFF);
        section.AsSpan(12, programInfoLength).Fill(0xFF);          // opaque program descriptors (skipped)
        int es = 12 + programInfoLength;
        section[es] = 0x1B;                                        // stream_type = H.264
        section[es + 1] = (byte)(0xE0 | (M2TSWriter.PidVideo >> 8));
        section[es + 2] = unchecked((byte)M2TSWriter.PidVideo);
        section[es + 3] = 0xF0;                                    // ES_info_length = 0
        section[es + 4] = 0x00;
        // section[es+5 .. +8] is the CRC32, left zero: the demuxer stops before it and never verifies.

        const int firstChunk = M2TSWriter.PacketSize - 5;          // 183 section bytes after header+pointer
        byte[] a = NewTsPacket(M2TSWriter.PidPmt, payloadUnitStart: true, cc: 0);
        a[4] = 0x00;                                               // pointer_field
        section.AsSpan(0, firstChunk).CopyTo(a.AsSpan(5));

        byte[] b = NewTsPacket(M2TSWriter.PidPmt, payloadUnitStart: false, cc: 1);
        section.AsSpan(firstChunk).CopyTo(b.AsSpan(4));            // the rest, then 0xFF stuffing

        return (a, b);
    }

    private static byte[] NewTsPacket(ushort pid, bool payloadUnitStart, byte cc)
    {
        var p = new byte[M2TSWriter.PacketSize];
        p.AsSpan(4).Fill(0xFF);                                    // payload stuffing by default
        p[0] = 0x47;
        p[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        p[2] = unchecked((byte)pid);
        p[3] = (byte)(0x10 | (cc & 0x0F));                         // payload only + continuity counter
        return p;
    }

    private static ushort PidOf(ReadOnlySpan<byte> ts, int packetIndex)
    {
        int o = packetIndex * M2TSWriter.PacketSize;
        return (ushort)(((ts[o + 1] & 0x1F) << 8) | ts[o + 2]);
    }

    /// <summary>Packets of the PES starting at packet <paramref name="index"/> (same PID, until the next PUSI).</summary>
    private static int PacketsOf(ReadOnlySpan<byte> ts, int index)
    {
        var count = 1;
        for (int i = index + 1; (i + 1) * M2TSWriter.PacketSize <= ts.Length; i++)
        {
            int o = i * M2TSWriter.PacketSize;
            if ((ts[o + 1] & 0x40) != 0)
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static int CountPackets(ReadOnlySpan<byte> ts, int from) =>
        ts.Length / M2TSWriter.PacketSize - from;
}
