using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

public class HLSPlaylistHandoverTests
{
    [Fact]
    public void HandoverContinuesTheSequenceWithADiscontinuity()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");

        var first = new HLSPlaylist(storage);
        first.AddSegment("seg00000.ts", 2.0);
        first.AddSegment("seg00001.ts", 2.0);

        HLSPlaylistHandover handover = first.ExportHandover();
        handover.Sequence.Should().Be(2);
        handover.Window.Should().HaveCount(2);

        // no ENDLIST was written: the playlist stays live across the takeover
        storage.Playlist.Should().NotContain("ENDLIST");

        var second = new HLSPlaylist(storage, resume: handover);
        second.NextSegmentName(".ts").Should().Be("seg00002.ts", "the media sequence continues");
        second.AddSegment("seg00002.ts", 2.0);

        string text = storage.Playlist!;
        text.Should().Contain("#EXT-X-MEDIA-SEQUENCE:0", "the window still starts at the first segment");
        text.Should().Contain("seg00000.ts").And.Contain("seg00001.ts").And.Contain("seg00002.ts");

        // exactly one discontinuity, placed before the successor's first segment
        text.Split("#EXT-X-DISCONTINUITY").Should().HaveCount(2);
        text.Should().Contain("seg00001.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:2.000,\nseg00002.ts");

        // only the second session ending normally finalizes the playlist
        second.Complete();
        storage.Playlist.Should().Contain("#EXT-X-ENDLIST");
    }

    [Fact]
    public void WindowTrimmingDeletesTheOldBlobs()
    {
        var root = new MemoryHLSStorage();
        IHLSStreamStorage storage = root.GetStream("trim");
        var playlist = new HLSPlaylist(storage);

        for (var i = 0; i < 8; i++) // window size is 6
        {
            string name = playlist.NextSegmentName(".ts");
            storage.WriteBlob(name, [0x47]);
            playlist.AddSegment(name, 2.0);
        }

        storage.TryReadBlob("seg00000.ts", out _).Should().BeFalse("trimmed out of the window");
        storage.TryReadBlob("seg00001.ts", out _).Should().BeFalse();
        storage.TryReadBlob("seg00002.ts", out _).Should().BeTrue();
        storage.TryReadBlob("seg00007.ts", out _).Should().BeTrue();
        storage.Playlist.Should().NotContain("seg00001.ts").And.Contain("seg00002.ts");

        root.TryGetStream("trim", out _).Should().BeTrue();
        root.TryGetStream("nosuch", out _).Should().BeFalse();
    }
}
