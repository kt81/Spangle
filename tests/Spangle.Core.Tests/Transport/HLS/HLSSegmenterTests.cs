using System.Buffers;
using Spangle.Containers.M2TS;
using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

/// <summary>
/// The TS segmenter consumes our own muxer's output (PAT/PMT written right before
/// every keyframe), cuts at the first keyframe whose PCR is at least the target
/// duration past the segment start, and maintains a sliding-window playlist.
/// </summary>
public class HLSSegmenterTests
{
    private static readonly byte[] s_keyAu = [0x00, 0x00, 0x00, 0x01, 0x65, 0x11, 0x22, 0x33];
    private static readonly byte[] s_pAu   = [0x00, 0x00, 0x00, 0x01, 0x41, 0x44, 0x55];

    /// <summary>Writes PAT+PMT followed by a keyframe PES, the way the live muxer does.</summary>
    private static void WriteKeyframeGroup(M2TSWriter muxer, ArrayBufferWriter<byte> ts, ulong pts)
    {
        muxer.WriteProgramTables(ts);
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_keyAu,
            pts, dts: null, randomAccess: true, withPcr: true);
    }

    private static void WritePFrame(M2TSWriter muxer, ArrayBufferWriter<byte> ts, ulong pts)
    {
        muxer.WritePes(ts, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo, s_pAu,
            pts, dts: null, randomAccess: false, withPcr: true);
    }

    private static void Feed(HLSSegmenter segmenter, ReadOnlySpan<byte> ts)
    {
        for (var i = 0; i < ts.Length; i += M2TSWriter.PacketSize)
        {
            segmenter.ProcessPacket(ts.Slice(i, M2TSWriter.PacketSize));
        }
    }

    [Fact]
    public void CutsAtTheFirstKeyframePastTheTargetDuration()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var segmenter = new HLSSegmenter(storage, targetDuration: 2.0);

        // Keyframes every second: the 1s boundary must NOT cut, the 2s one must
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        WriteKeyframeGroup(muxer, ts, pts: 0);
        WritePFrame(muxer, ts, pts: 45_000);
        WriteKeyframeGroup(muxer, ts, pts: 90_000);   // 1.0 s: below target, no cut
        WriteKeyframeGroup(muxer, ts, pts: 180_000);  // 2.0 s: cut here
        WritePFrame(muxer, ts, pts: 225_000);

        Feed(segmenter, ts.WrittenSpan);
        segmenter.Complete();

        string playlist = storage.Playlist!;
        playlist.Should().Contain("#EXTINF:2.000,\nseg00000.ts",
            "the segment closes at the first keyframe at/after the 2s target, not at the 1s keyframe");
        playlist.Should().Contain("#EXTINF:0.500,\nseg00001.ts", "Complete flushes the remainder");
        playlist.Should().Contain("#EXT-X-MEDIA-SEQUENCE:0");
        playlist.Should().EndWith("#EXT-X-ENDLIST\n");

        // Both segments exist, are whole packets, and start with PAT+PMT then a video packet
        foreach (string name in (string[])["seg00000.ts", "seg00001.ts"])
        {
            storage.TryReadBlob(name, out ReadOnlyMemory<byte> segMemory).Should().BeTrue();
            byte[] seg = segMemory.ToArray();
            (seg.Length % M2TSWriter.PacketSize).Should().Be(0);
            PidOf(seg, 0).Should().Be(M2TSWriter.PidPat, $"{name} must start with a PAT");
            PidOf(seg, 1).Should().Be(M2TSWriter.PidPmt);
            PidOf(seg, 2).Should().Be(M2TSWriter.PidVideo);
        }

        // The tables held while deciding the no-cut at 1s must stay in segment 0:
        // it contains all three PATs written before the cut boundary's tables
        storage.TryReadBlob("seg00000.ts", out ReadOnlyMemory<byte> seg0).Should().BeTrue();
        CountPid(seg0.ToArray(), M2TSWriter.PidPat).Should().Be(2, "the 0s and 1s tables belong to segment 0");
        storage.TryReadBlob("seg00001.ts", out ReadOnlyMemory<byte> seg1).Should().BeTrue();
        CountPid(seg1.ToArray(), M2TSWriter.PidPat).Should().Be(1, "the 2s tables lead segment 1");
    }

    [Fact]
    public void TrimsTheWindowAndDeletesOldSegmentsFromStorage()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var segmenter = new HLSSegmenter(storage, targetDuration: 2.0, windowSize: 2);

        // Keyframes 2.5s apart: every boundary cuts; 4 groups + a tail -> 4 segments
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        for (var i = 0; i < 4; i++)
        {
            WriteKeyframeGroup(muxer, ts, pts: (ulong)i * 225_000);
        }
        WritePFrame(muxer, ts, pts: 720_000); // flushed by Complete as the 4th segment

        Feed(segmenter, ts.WrittenSpan);
        segmenter.Complete();

        string playlist = storage.Playlist!;
        playlist.Should().Contain("#EXT-X-MEDIA-SEQUENCE:2", "two segments fell out of the window");
        playlist.Should().NotContain("seg00000.ts").And.NotContain("seg00001.ts");
        playlist.Should().Contain("seg00002.ts").And.Contain("seg00003.ts");

        storage.TryReadBlob("seg00000.ts", out _).Should().BeFalse("trimmed segments are deleted from storage");
        storage.TryReadBlob("seg00001.ts", out _).Should().BeFalse();
        storage.TryReadBlob("seg00002.ts", out _).Should().BeTrue();
        storage.TryReadBlob("seg00003.ts", out _).Should().BeTrue();
    }

    [Fact]
    public void ExportHandoverFlushesWithoutEndListAndCarriesTheWindow()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var segmenter = new HLSSegmenter(storage, targetDuration: 2.0);

        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        WriteKeyframeGroup(muxer, ts, pts: 0);
        WriteKeyframeGroup(muxer, ts, pts: 225_000); // cuts segment 0 at 2.5s
        WritePFrame(muxer, ts, pts: 270_000);

        Feed(segmenter, ts.WrittenSpan);
        HLSPlaylistHandover handover = segmenter.ExportHandover();

        handover.Sequence.Should().Be(2, "the remainder was flushed as the second segment");
        handover.Window.Should().HaveCount(2);
        handover.Window[0].Name.Should().Be("seg00000.ts");
        handover.Window[0].Duration.Should().BeApproximately(2.5, 0.001);
        handover.Window[1].Name.Should().Be("seg00001.ts");

        storage.Playlist.Should().NotContain("#EXT-X-ENDLIST", "a takeover keeps the playlist live");
        storage.TryReadBlob("seg00001.ts", out _).Should().BeTrue();
    }

    [Fact]
    public void ResumedSegmenterContinuesTheSequenceWithADiscontinuity()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var first = new HLSSegmenter(storage, targetDuration: 2.0);

        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        WriteKeyframeGroup(muxer, ts, pts: 0);
        WriteKeyframeGroup(muxer, ts, pts: 225_000);
        Feed(first, ts.WrittenSpan);
        HLSPlaylistHandover handover = first.ExportHandover();

        // the successor session restarts its own timeline at zero
        var second = new HLSSegmenter(storage, targetDuration: 2.0, resume: handover);
        var muxer2 = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts2 = new ArrayBufferWriter<byte>();
        WriteKeyframeGroup(muxer2, ts2, pts: 0);
        WriteKeyframeGroup(muxer2, ts2, pts: 225_000);
        Feed(second, ts2.WrittenSpan);
        second.Complete();

        string playlist = storage.Playlist!;
        playlist.Should().Contain("seg00002.ts", "the media sequence continues after the takeover");
        playlist.Should().Contain("#EXT-X-DISCONTINUITY", "players must expect a timestamp jump");
        playlist.Should().Contain("#EXT-X-ENDLIST");
    }

    [Fact]
    public void SegmentDurationSurvivesThePcrWrap()
    {
        // The 33-bit PCR base wraps every ~26.5 hours. A cut boundary straddling the wrap
        // used to underflow the subtraction into a duration of ~2×10^14 seconds — mis-cutting
        // the segment and, because the target-duration ceiling only ever rises, poisoning the
        // playlist for the rest of the stream.
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var segmenter = new HLSSegmenter(storage, targetDuration: 2.0);

        const ulong wrap = 1UL << 33;
        var muxer = new M2TSWriter { VideoCodec = VideoCodec.H264 };
        var ts = new ArrayBufferWriter<byte>();
        WriteKeyframeGroup(muxer, ts, pts: wrap - 45_000); // 0.5 s before the wrap
        WriteKeyframeGroup(muxer, ts, pts: 135_000);       // 1.5 s after it: 2.0 s apart, so cut
        WritePFrame(muxer, ts, pts: 180_000);

        Feed(segmenter, ts.WrittenSpan);
        segmenter.Complete();

        string playlist = storage.Playlist!;
        playlist.Should().Contain("#EXTINF:2.000,\nseg00000.ts",
            "the duration must be the masked 33-bit distance, not a raw subtraction");
        playlist.Should().Contain("#EXT-X-TARGETDURATION:2",
            "an astronomical duration would raise the ceiling for the rest of the stream");
    }

    [Fact]
    public void BrokenPacketStreamIsRejected()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        var segmenter = new HLSSegmenter(storage, targetDuration: 2.0);

        var badSync = new byte[M2TSWriter.PacketSize];
        badSync[0] = 0x48;
        Action wrongSync = () => segmenter.ProcessPacket(badSync);
        wrongSync.Should().Throw<InvalidDataException>();

        var truncated = new byte[M2TSWriter.PacketSize - 1];
        truncated[0] = 0x47;
        Action shortPacket = () => segmenter.ProcessPacket(truncated);
        shortPacket.Should().Throw<InvalidDataException>();
    }

    // =======================================================================

    private static ushort PidOf(ReadOnlySpan<byte> ts, int packetIndex)
    {
        int o = packetIndex * M2TSWriter.PacketSize;
        return (ushort)(((ts[o + 1] & 0x1F) << 8) | ts[o + 2]);
    }

    private static int CountPid(ReadOnlySpan<byte> ts, ushort pid)
    {
        var count = 0;
        for (var i = 0; i < ts.Length / M2TSWriter.PacketSize; i++)
        {
            if (PidOf(ts, i) == pid)
            {
                count++;
            }
        }
        return count;
    }
}
