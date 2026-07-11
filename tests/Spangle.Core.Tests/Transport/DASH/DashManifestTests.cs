using System.Text;
using Spangle.Containers.ISOBMFF;
using Spangle.Transport.DASH;
using Spangle.Transport.HLS;

namespace Spangle.Tests.Transport.DASH;

public class DashManifestTests
{
    [Fact]
    public void DynamicMpdCarriesPerTrackAdaptationSets()
    {
        var storage = new MemoryHLSStorage().GetStream("test");
        var dash = new DashManifest(storage) { TargetSegmentDuration = 2.0 };
        dash.Tracks.Add(new DashTrack
        {
            MimeType = "video/mp4", Codecs = "avc1.64001F", InitName = "init_v.mp4",
            SegmentPrefix = "segV", Width = 640, Height = 360,
        });
        dash.Tracks.Add(new DashTrack
        {
            MimeType = "audio/mp4", Codecs = "mp4a.40.2", InitName = "init_a.mp4", SegmentPrefix = "segA",
        });
        var playlist = new HLSPlaylist(storage, "init_v.mp4", dash: dash) { SegmentNamePrefix = "segV" };

        playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);
        playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.5);

        string mpd = ReadMpd(storage);
        mpd.Should().Contain("type=\"dynamic\"");
        mpd.Should().Contain("mimeType=\"video/mp4\" codecs=\"avc1.64001F\"");
        mpd.Should().Contain("mimeType=\"audio/mp4\" codecs=\"mp4a.40.2\"");
        mpd.Should().Contain("initialization=\"init_v.mp4\"").And.Contain("initialization=\"init_a.mp4\"");
        mpd.Should().Contain("media=\"segV$Number%05d$.m4s\" startNumber=\"0\"");
        mpd.Should().Contain("media=\"segA$Number%05d$.m4s\" startNumber=\"0\"");
        mpd.Should().Contain("<S t=\"0\" d=\"2000\"/>");
        mpd.Should().Contain("<S t=\"2000\" d=\"2500\"/>", "starts accumulate through the timeline");
        mpd.Should().Contain("maxSegmentDuration=\"PT2.5S\"");
        mpd.Should().Contain("width=\"640\" height=\"360\"");
    }

    [Fact]
    public void EndedStreamsBecomeStatic()
    {
        var storage = new MemoryHLSStorage().GetStream("test");
        var dash = new DashManifest(storage);
        dash.Tracks.Add(new DashTrack
        {
            MimeType = "video/mp4", Codecs = "avc1.64001F", InitName = "init_v.mp4", SegmentPrefix = "segV",
        });
        var playlist = new HLSPlaylist(storage, "init_v.mp4", dash: dash) { SegmentNamePrefix = "segV" };

        playlist.AddSegment(playlist.NextSegmentName(".m4s"), 2.0);
        playlist.Complete();

        string mpd = ReadMpd(storage);
        mpd.Should().Contain("type=\"static\"");
        mpd.Should().Contain("mediaPresentationDuration=\"PT2S\"");
        mpd.Should().NotContain("minimumUpdatePeriod");
    }

    [Fact]
    public void TakeoverKeepsTheTimelineAndTheClockAnchor()
    {
        var storage = new MemoryHLSStorage().GetStream("test");
        var dash = new DashManifest(storage);
        dash.Tracks.Add(new DashTrack
        {
            MimeType = "video/mp4", Codecs = "avc1.64001F", InitName = "init_v.mp4", SegmentPrefix = "segV",
        });
        var first = new HLSPlaylist(storage, "init_v.mp4", dash: dash) { SegmentNamePrefix = "segV" };
        first.AddSegment(first.NextSegmentName(".m4s"), 2.0);
        string astBefore = ExtractAst(ReadMpd(storage));

        HLSPlaylistHandover handover = first.ExportHandover();
        var second = new HLSPlaylist(storage, "init_v.mp4", resume: handover, dash: dash)
        {
            SegmentNamePrefix = "segV",
        };
        second.AddSegment(second.NextSegmentName(".m4s"), 2.0);

        string mpd = ReadMpd(storage);
        ExtractAst(mpd).Should().Be(astBefore, "the wall-clock anchor survives the takeover");
        mpd.Should().Contain("<S t=\"2000\" d=\"2000\"/>", "the successor continues the media timeline");
    }

    [Fact]
    public void CodecStringsAreDerivedFromConfigRecords()
    {
        // avcC: version, profile 0x64, compat 0x00, level 0x1F
        byte[] avcc = [0x01, 0x64, 0x00, 0x1F, 0xFF, 0xE1];
        CodecStrings.FromAvcC(avcc).Should().Be("avc1.64001F");

        // hvcC head: PTL 0x01 (space0/tier L/idc1), compat 0x60000000, constraints 90 00.., level 93
        byte[] hvcc =
        [
            0x01, 0x01, 0x60, 0x00, 0x00, 0x00, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 93,
        ];
        CodecStrings.FromHvcC(hvcc).Should().Be("hvc1.1.6.L93.90");

        CodecStrings.FromAudio(AudioCodec.AAC, [0x11, 0x90]).Should().Be("mp4a.40.2");
        CodecStrings.FromAudio(AudioCodec.Opus, []).Should().Be("opus");
    }

    private static string ReadMpd(IHLSStreamStorage storage)
    {
        storage.TryReadBlob("manifest.mpd", out ReadOnlyMemory<byte> blob).Should().BeTrue();
        return Encoding.UTF8.GetString(blob.Span);
    }

    private static string ExtractAst(string mpd)
    {
        int at = mpd.IndexOf("availabilityStartTime=\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        int start = at + "availabilityStartTime=\"".Length;
        return mpd[start..mpd.IndexOf('"', start)];
    }
}
