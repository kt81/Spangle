using System.Globalization;
using System.Text;
using Spangle.Transport.HLS;

namespace Spangle.Transport.DASH;

/// <summary>One AdaptationSet of the MPD: a demuxed CMAF track sequence.</summary>
internal sealed class DashTrack
{
    /// <summary>"video/mp4" or "audio/mp4"</summary>
    public required string MimeType { get; init; }

    /// <summary>RFC 6381 codec string</summary>
    public required string Codecs { get; init; }

    /// <summary>Init segment blob name (init_v.mp4 / init_a.mp4)</summary>
    public required string InitName { get; init; }

    /// <summary>Segment name prefix (segV / segA), matching the HLS media playlist</summary>
    public required string SegmentPrefix { get; init; }

    public uint Width { get; init; }
    public uint Height { get; init; }

    /// <summary>Measured output rate; refreshed per segment</summary>
    public long Bandwidth { get; set; } = 1_000_000;
}

/// <summary>
/// Renders a live-profile MPD over the demuxed CMAF tracks the HLS side already
/// produces — the segments are shared byte for byte, so DASH costs one more
/// manifest. The tracks are segment-aligned by construction (one cut drives all),
/// so a single timeline serves every AdaptationSet. Published as a
/// <c>manifest.mpd</c> blob on every playlist update.
/// </summary>
internal sealed class DashManifest(IHLSStreamStorage storage)
{
    public List<DashTrack> Tracks { get; } = new();

    public double TargetSegmentDuration { get; set; } = 2.0;

    /// <summary>
    /// Enables LL-DASH signaling: fixed-duration SegmentTemplate arithmetic with
    /// availabilityTimeOffset, so players fetch the in-progress segment (served over
    /// chunked transfer) one part after it starts. Requires a near-constant segment
    /// duration, i.e. a steady keyframe interval from the encoder.
    /// </summary>
    public double? PartTargetDuration { get; set; }

    private readonly StringBuilder _sb = new(2048);

    public void Publish(IReadOnlyList<HLSPlaylist.SegmentEntry> window, int sequence, bool ended,
        DateTime availabilityStart)
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        StringBuilder sb = _sb.Clear();
        double windowSeconds = window.Sum(static w => w.Duration);
        double maxSegment = window.Max(static w => w.Duration);

        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"");
        sb.Append(" profiles=\"urn:mpeg:dash:profile:isoff-live:2011\"");
        if (ended)
        {
            sb.Append(" type=\"static\"");
            sb.Append(CultureInfo.InvariantCulture, $" mediaPresentationDuration=\"{Pt(window[^1].Start + window[^1].Duration)}\"");
        }
        else
        {
            sb.Append(" type=\"dynamic\"");
            sb.Append(CultureInfo.InvariantCulture, $" availabilityStartTime=\"{Iso(availabilityStart)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" publishTime=\"{Iso(DateTime.UtcNow)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" minimumUpdatePeriod=\"{Pt(TargetSegmentDuration)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" timeShiftBufferDepth=\"{Pt(windowSeconds)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" suggestedPresentationDelay=\"{Pt(TargetSegmentDuration * 3)}\"");
        }
        sb.Append(CultureInfo.InvariantCulture, $" maxSegmentDuration=\"{Pt(maxSegment)}\"");
        sb.Append(CultureInfo.InvariantCulture, $" minBufferTime=\"{Pt(TargetSegmentDuration)}\">\n");

        bool lowLatency = PartTargetDuration is not null && !ended;
        if (lowLatency)
        {
            // clients must share the server clock to compute the live edge
            sb.Append("  <UTCTiming schemeIdUri=\"urn:mpeg:dash:utc:http-xsdate:2014\" value=\"/api/time\"/>\n");
            sb.Append("  <ServiceDescription id=\"0\">\n");
            sb.Append(CultureInfo.InvariantCulture, $"    <Latency target=\"{(long)(TargetSegmentDuration * 1500)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" min=\"{(long)(PartTargetDuration!.Value * 2000)}\"");
            sb.Append(CultureInfo.InvariantCulture, $" max=\"{(long)(TargetSegmentDuration * 4000)}\"/>\n");
            sb.Append("    <PlaybackRate min=\"0.96\" max=\"1.04\"/>\n");
            sb.Append("  </ServiceDescription>\n");
        }

        sb.Append("  <Period id=\"0\" start=\"PT0S\">\n");

        var trackId = 1;
        foreach (DashTrack track in Tracks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <AdaptationSet mimeType=\"{track.MimeType}\" codecs=\"{track.Codecs}\"");
            sb.Append(" segmentAlignment=\"true\">\n");

            if (lowLatency)
            {
                // Fixed-duration arithmetic: number 0 covers media time [0, d), so
                // the in-progress segment is addressable before it completes. The
                // offset makes it available one part after its start.
                double ato = TargetSegmentDuration - PartTargetDuration!.Value;
                sb.Append(CultureInfo.InvariantCulture, $"      <SegmentTemplate timescale=\"1000\" initialization=\"{track.InitName}\"");
                sb.Append(CultureInfo.InvariantCulture, $" media=\"{track.SegmentPrefix}$Number%05d$.m4s\" startNumber=\"0\"");
                sb.Append(CultureInfo.InvariantCulture, $" duration=\"{(long)(TargetSegmentDuration * 1000)}\"");
                sb.Append(CultureInfo.InvariantCulture, $" availabilityTimeOffset=\"{ato:0.###}\"");
                sb.Append(" availabilityTimeComplete=\"false\"/>\n");
            }
            else
            {
                int startNumber = sequence - window.Count;
                sb.Append(CultureInfo.InvariantCulture, $"      <SegmentTemplate timescale=\"1000\" initialization=\"{track.InitName}\"");
                sb.Append(CultureInfo.InvariantCulture, $" media=\"{track.SegmentPrefix}$Number%05d$.m4s\" startNumber=\"{startNumber}\">\n");
                sb.Append("        <SegmentTimeline>\n");
                foreach (HLSPlaylist.SegmentEntry entry in window)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"          <S t=\"{(long)(entry.Start * 1000)}\" d=\"{(long)(entry.Duration * 1000)}\"/>\n");
                }
                sb.Append("        </SegmentTimeline>\n");
                sb.Append("      </SegmentTemplate>\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"      <Representation id=\"{trackId++}\" bandwidth=\"{track.Bandwidth}\"");
            if (track.Width > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" width=\"{track.Width}\" height=\"{track.Height}\"");
            }
            sb.Append("/>\n");
            sb.Append("    </AdaptationSet>\n");
        }

        sb.Append("  </Period>\n");
        sb.Append("</MPD>\n");

        storage.WriteBlob("manifest.mpd", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Pt(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"PT{seconds:0.###}S");
}
