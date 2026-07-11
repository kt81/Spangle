using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.HLS;

/// <summary>
/// LL-HLS playlist delta updates (RFC 8216bis 6.2.5.1): segments older than
/// CAN-SKIP-UNTIL collapse into EXT-X-SKIP; the client splices with its cached copy.
/// </summary>
public class PlaylistDeltaUpdateTests
{
    [Fact]
    public void DeltaCollapsesOldSegmentsIntoSkip()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        string? lastDelta = null;
        var playlist = new HLSPlaylist(storage, partTargetDuration: 0.5,
            onUpdated: (_, delta, _, _) => lastDelta = delta, windowSize: 12);

        for (var i = 0; i < 12; i++)
        {
            playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);
        }

        // CAN-SKIP-UNTIL = 6 x 2s = 12s; the tail 6 segments must stay, 6 are skippable
        lastDelta.Should().NotBeNull();
        lastDelta.Should().Contain("#EXT-X-SKIP:SKIPPED-SEGMENTS=6\n");
        lastDelta.Should().Contain("#EXT-X-VERSION:9");
        lastDelta.Should().NotContain("seg00005.m4s", "skipped segments are not listed");
        lastDelta.Should().Contain("seg00006.m4s", "the first kept segment");
        lastDelta.Should().Contain("#EXT-X-MEDIA-SEQUENCE:0",
            "the media sequence still describes the full playlist");

        // the full playlist advertises the capability and keeps every line
        string full = storage.Playlist!;
        full.Should().Contain("CAN-SKIP-UNTIL=12.0");
        full.Should().Contain("seg00000.m4s").And.NotContain("EXT-X-SKIP");
    }

    [Fact]
    public void NoDeltaWhileEverythingIsWithinTheKeepRange()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        string? lastDelta = null;
        var playlist = new HLSPlaylist(storage, partTargetDuration: 0.5,
            onUpdated: (_, delta, _, _) => lastDelta = delta, windowSize: 12);

        for (var i = 0; i < 6; i++) // 12s of segments = exactly the keep range
        {
            playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);
        }

        lastDelta.Should().BeNull();
    }

    [Fact]
    public void NoDeltaForRegularHlsPlaylists()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        string? lastDelta = "sentinel";
        var playlist = new HLSPlaylist(storage,
            onUpdated: (_, delta, _, _) => lastDelta = delta, windowSize: 24);

        for (var i = 0; i < 24; i++)
        {
            playlist.AddSegment(playlist.NextSegmentName(".ts"), 2.0);
        }

        lastDelta.Should().BeNull("delta updates only exist alongside LL-HLS server control");
        storage.Playlist.Should().NotContain("CAN-SKIP-UNTIL");
    }

    [Fact]
    public void DiscontinuityInTheSkippedRangeFallsBackToTheFullPlaylist()
    {
        IHLSStreamStorage storage = new MemoryHLSStorage().GetStream("test");
        string? lastDelta = null;
        var playlist = new HLSPlaylist(storage, partTargetDuration: 0.5,
            onUpdated: (_, delta, _, _) => lastDelta = delta, windowSize: 12);

        playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);
        HLSPlaylistHandover handover = playlist.ExportHandover();
        var successor = new HLSPlaylist(storage, partTargetDuration: 0.5,
            onUpdated: (_, delta, _, _) => lastDelta = delta, resume: handover, windowSize: 12);
        for (var i = 0; i < 11; i++) // the discontinuity entry stays inside the window
        {
            successor.AddSegment(successor.NextSegmentName(".m4s"), 2.0);
        }

        // 12 entries, 6 skippable — but the discontinuity sits at entry 1 (skipped range)
        lastDelta.Should().BeNull("skipping across a discontinuity would corrupt the client's model");
    }
}
