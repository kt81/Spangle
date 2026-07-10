using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

public class HLSPlaylistHandoverTests
{
    [Fact]
    public void HandoverContinuesTheSequenceWithADiscontinuity()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spangle-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = new HLSPlaylist(dir);
            first.AddSegment("seg00000.ts", 2.0);
            first.AddSegment("seg00001.ts", 2.0);

            HLSPlaylistHandover handover = first.ExportHandover();
            handover.Sequence.Should().Be(2);
            handover.Window.Should().HaveCount(2);

            // no ENDLIST was written: the playlist stays live across the takeover
            File.ReadAllText(Path.Combine(dir, "playlist.m3u8")).Should().NotContain("ENDLIST");

            var second = new HLSPlaylist(dir, resume: handover);
            second.NextSegmentName(".ts").Should().Be("seg00002.ts", "the media sequence continues");
            second.AddSegment("seg00002.ts", 2.0);

            string text = File.ReadAllText(Path.Combine(dir, "playlist.m3u8"));
            text.Should().Contain("#EXT-X-MEDIA-SEQUENCE:0", "the window still starts at the first segment");
            text.Should().Contain("seg00000.ts").And.Contain("seg00001.ts").And.Contain("seg00002.ts");

            // exactly one discontinuity, placed before the successor's first segment
            text.Split("#EXT-X-DISCONTINUITY").Should().HaveCount(2);
            text.Should().Contain("seg00001.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:2.000,\nseg00002.ts");

            // only the second session ending normally finalizes the playlist
            second.Complete();
            File.ReadAllText(Path.Combine(dir, "playlist.m3u8")).Should().Contain("#EXT-X-ENDLIST");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
