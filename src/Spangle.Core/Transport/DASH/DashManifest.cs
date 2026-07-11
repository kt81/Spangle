using System.Text;
using Spangle.Transport.HLS;

namespace Spangle.Transport.DASH;

/// <summary>
/// Renders a live-profile MPD over the CMAF output the HLS side already produces —
/// the segments are shared byte for byte, so DASH costs one more manifest.
/// SegmentTimeline (timescale 1000) carries the variable segment durations; the
/// media template matches the existing seg%05d naming. Published as a
/// <c>manifest.mpd</c> blob on every playlist update.
/// </summary>
internal sealed class DashManifest(IHLSStreamStorage storage, string initName)
{
    /// <summary>RFC 6381 codec strings; null when the track does not exist</summary>
    public string? VideoCodecString { get; set; }

    public string? AudioCodecString { get; set; }

    public uint Width { get; set; }
    public uint Height { get; set; }

    public double TargetSegmentDuration { get; set; } = 2.0;

    private long _bandwidth = 1_000_000;
    private readonly StringBuilder _sb = new(2048);

    /// <summary>Feeds the measured output rate into the Representation@bandwidth attribute.</summary>
    public void UpdateBandwidth(long segmentBytes, double segmentSeconds)
    {
        if (segmentSeconds > 0.1)
        {
            _bandwidth = (long)(segmentBytes * 8 / segmentSeconds);
        }
    }

    public void Publish(IReadOnlyList<HLSPlaylist.SegmentEntry> window, int sequence, bool ended,
        DateTime availabilityStart)
    {
        StringBuilder sb = _sb.Clear();
        double windowSeconds = window.Sum(static w => w.Duration);
        double maxSegment = window.Max(static w => w.Duration);

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"");
        sb.Append(" profiles=\"urn:mpeg:dash:profile:isoff-live:2011\"");
        if (ended)
        {
            sb.Append(" type=\"static\"");
            sb.Append($" mediaPresentationDuration=\"{Pt(window[^1].Start + window[^1].Duration)}\"");
        }
        else
        {
            sb.Append(" type=\"dynamic\"");
            sb.Append($" availabilityStartTime=\"{Iso(availabilityStart)}\"");
            sb.Append($" publishTime=\"{Iso(DateTime.UtcNow)}\"");
            sb.Append($" minimumUpdatePeriod=\"{Pt(TargetSegmentDuration)}\"");
            sb.Append($" timeShiftBufferDepth=\"{Pt(windowSeconds)}\"");
            sb.Append($" suggestedPresentationDelay=\"{Pt(TargetSegmentDuration * 3)}\"");
        }
        sb.Append($" maxSegmentDuration=\"{Pt(maxSegment)}\"");
        sb.Append($" minBufferTime=\"{Pt(TargetSegmentDuration)}\">\n");

        sb.Append("  <Period id=\"0\" start=\"PT0S\">\n");

        // The output is muxed CMAF: one AdaptationSet carries every track
        string mimeType = VideoCodecString is not null ? "video/mp4" : "audio/mp4";
        string codecs = string.Join(',',
            new[] { VideoCodecString, AudioCodecString }.Where(static c => c is not null));
        sb.Append($"    <AdaptationSet mimeType=\"{mimeType}\" codecs=\"{codecs}\" segmentAlignment=\"true\">\n");

        int startNumber = sequence - window.Count;
        sb.Append($"      <SegmentTemplate timescale=\"1000\" initialization=\"{initName}\"");
        sb.Append($" media=\"seg$Number%05d$.m4s\" startNumber=\"{startNumber}\">\n");
        sb.Append("        <SegmentTimeline>\n");
        foreach (HLSPlaylist.SegmentEntry entry in window)
        {
            sb.Append($"          <S t=\"{(long)(entry.Start * 1000)}\" d=\"{(long)(entry.Duration * 1000)}\"/>\n");
        }
        sb.Append("        </SegmentTimeline>\n");
        sb.Append("      </SegmentTemplate>\n");

        sb.Append($"      <Representation id=\"1\" bandwidth=\"{_bandwidth}\"");
        if (VideoCodecString is not null && Width > 0)
        {
            sb.Append($" width=\"{Width}\" height=\"{Height}\"");
        }
        sb.Append("/>\n");

        sb.Append("    </AdaptationSet>\n");
        sb.Append("  </Period>\n");
        sb.Append("</MPD>\n");

        storage.WriteBlob("manifest.mpd", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static string Pt(double seconds) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PT{seconds:0.###}S");
}
