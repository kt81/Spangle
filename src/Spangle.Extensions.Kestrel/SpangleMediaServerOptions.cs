using System.ComponentModel.DataAnnotations;

namespace Spangle.Extensions.Kestrel;

public class SpangleMediaServerOptions
{
    /// <summary>
    /// The path of the section to bind in the setting file
    /// </summary>
    public const string SectionPath = "Spangle";

    public RtmpOptions Rtmp { get; set; }
}

public class RtmpOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 1935;
}

public class HlsOptions : MediaProtocolOptions
{
    public string SegmentFormat { get; set; } = "TS";
    public string SegmentDuration { get; set; } = "";
}

public abstract class MediaProtocolOptions
{
    public bool Enabled { get; set; }
}
