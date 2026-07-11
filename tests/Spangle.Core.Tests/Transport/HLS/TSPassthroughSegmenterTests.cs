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
        string dir = Path.Combine(Path.GetTempPath(), "spangle-passthrough-" + Guid.NewGuid().ToString("N"));
        try
        {
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

            var segmenter = new TSPassthroughSegmenter(dir, targetDuration: 2.0);
            ReadOnlySpan<byte> written = ts.WrittenSpan;
            for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
            {
                segmenter.ProcessPacket(written.Slice(i, M2TSWriter.PacketSize));
            }
            segmenter.Complete();

            string playlist = File.ReadAllText(Path.Combine(dir, "playlist.m3u8"));
            playlist.Should().Contain("#EXTINF:2.500,\nseg00000.ts", "the second keyframe closes the first segment");
            playlist.Should().Contain("seg00001.ts");
            playlist.Should().Contain("#EXT-X-ENDLIST");

            // Both segments must start with PAT + PMT + a random-access video packet
            foreach (string name in (string[])["seg00000.ts", "seg00001.ts"])
            {
                byte[] seg = File.ReadAllBytes(Path.Combine(dir, name));
                (seg.Length % M2TSWriter.PacketSize).Should().Be(0);
                PidOf(seg, 0).Should().Be(0x0000, $"{name} must start with the injected PAT");
                PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt, $"{name} continues with the injected PMT");
                PidOf(seg, 2).Should().Be(M2TSWriter.PidVideo);
                (seg[2 * M2TSWriter.PacketSize + 1] & 0x40).Should().NotBe(0, "the video packet starts a PES");
            }

            // Injected PSI continuity counters must be gapless across segments
            byte patCc0 = (byte)(File.ReadAllBytes(Path.Combine(dir, "seg00000.ts"))[3] & 0x0F);
            byte patCc1 = (byte)(File.ReadAllBytes(Path.Combine(dir, "seg00001.ts"))[3] & 0x0F);
            patCc1.Should().Be((byte)((patCc0 + 1) & 0x0F));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void DropsTheHeadUntilTheFirstRandomAccessPoint()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spangle-passthrough-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The stream joins mid-GOP: a non-key AU comes before the first keyframe
            var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
            var ts = new ArrayBufferWriter<byte>();
            muxer.WriteProgramTables(ts);
            muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_pAu,
                pts: 0, dts: null, randomAccess: false, withPcr: true);
            muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
                pts: 90_000, dts: null, randomAccess: true, withPcr: true);

            var segmenter = new TSPassthroughSegmenter(dir, targetDuration: 2.0);
            ReadOnlySpan<byte> written = ts.WrittenSpan;
            for (var i = 0; i < written.Length; i += M2TSWriter.PacketSize)
            {
                segmenter.ProcessPacket(written.Slice(i, M2TSWriter.PacketSize));
            }
            segmenter.Complete();

            byte[] seg = File.ReadAllBytes(Path.Combine(dir, "seg00000.ts"));
            PidOf(seg, 0).Should().Be(0x0000);
            PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt);
            // the mid-GOP P-frame must not be in the segment: 2 PSI + the keyframe PES only
            int videoPackets = Enumerable.Range(2, seg.Length / M2TSWriter.PacketSize - 2)
                .Count(i => PidOf(seg, i) == M2TSWriter.PidVideo);
            byte[] source = ts.WrittenSpan.ToArray();
            int keyframePackets = CountPackets(source, from: 2 + PacketsOf(source, 2)); // packets of the second PES
            videoPackets.Should().Be(keyframePackets, "only the keyframe access unit is kept");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // =======================================================================

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
