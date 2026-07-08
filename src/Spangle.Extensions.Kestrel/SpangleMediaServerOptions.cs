using System.ComponentModel.DataAnnotations;

namespace Spangle.Extensions.Kestrel;

public class SpangleMediaServerOptions
{
    /// <summary>
    /// The path of the section to bind in the setting file
    /// </summary>
    public const string SectionPath = "Spangle";

    public RtmpOptions Rtmp { get; set; } = new();
    public HlsOptions Hls { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
}

public class RtmpOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 1935;
}

public class HttpOptions
{
    /// <summary>Port for HTTP delivery (HLS files and the test player)</summary>
    [Range(1, 65535)] public int Port { get; set; } = 8080;
}

public class HlsOptions : MediaProtocolOptions
{
    /// <summary>Segment container: "TS" (MPEG-2 TS) or "fMP4" (CMAF)</summary>
    public string SegmentFormat { get; set; } = "TS";

    /// <summary>Directory where segments and playlists are written</summary>
    public string OutputDirectory { get; set; } = "hls-out";

    /// <summary>HTTP path prefix the HLS files are served under</summary>
    public string RequestPath { get; set; } = "/hls";

    /// <summary>Minimum segment duration in seconds; segments are cut at the first keyframe after this</summary>
    [Range(0.5, 60.0)] public double TargetSegmentDuration { get; set; } = 2.0;

    /// <summary>Enables LL-HLS partial segments and blocking playlist reload (fMP4 only)</summary>
    public bool LowLatency { get; set; }

    /// <summary>Target duration of LL-HLS partial segments in seconds</summary>
    [Range(0.1, 5.0)] public double PartTargetDuration { get; set; } = 0.5;
}

public abstract class MediaProtocolOptions
{
    public bool Enabled { get; set; }
}
