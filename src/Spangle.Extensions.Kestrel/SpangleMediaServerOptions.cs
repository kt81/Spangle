using System.ComponentModel.DataAnnotations;

namespace Spangle.Extensions.Kestrel;

public class SpangleMediaServerOptions
{
    /// <summary>
    /// The path of the section to bind in the setting file
    /// </summary>
    public const string SectionPath = "Spangle";

    public RtmpOptions Rtmp { get; set; } = new();
    public SrtOptions Srt { get; set; } = new();
    public HlsOptions Hls { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
}

public class RtmpOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 1935;

    /// <summary>
    /// RTMP cannot declare "no video is coming", so a session with audio but no video
    /// codec after this many milliseconds is treated as audio-only (radio publishers).
    /// 0 disables the fallback.
    /// </summary>
    [Range(0, 60_000)] public int AudioOnlyFallbackMs { get; set; } = 3000;

    /// <summary>
    /// Converts AMF0 data events (onTextData, cue points, ...) into timed ID3
    /// metadata carried in the HLS output. Adds one spinner hop to the pipeline.
    /// </summary>
    public bool TimedMetadata { get; set; } = true;
}

public class SrtOptions : MediaProtocolOptions
{
    [Range(1025, 65535)] public int Port { get; set; } = 9998;

    /// <summary>
    /// Pre-shared passphrase (10-79 bytes) for SRT encryption; senders presenting a
    /// different passphrase are rejected. Null accepts unencrypted connections only.
    /// </summary>
    public string? Passphrase { get; set; }
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

    /// <summary>
    /// Output backend: "Memory" (default; the live window is served from process
    /// memory, nothing touches disk) or "File" (segments persist under
    /// <see cref="OutputDirectory"/> as an archive, like before)
    /// </summary>
    public string Storage { get; set; } = "Memory";

    /// <summary>Directory where segments and playlists are written (File storage)</summary>
    public string OutputDirectory { get; set; } = "hls-out";

    /// <summary>HTTP path prefix the HLS files are served under</summary>
    public string RequestPath { get; set; } = "/hls";

    /// <summary>Minimum segment duration in seconds; segments are cut at the first keyframe after this</summary>
    [Range(0.5, 60.0)] public double TargetSegmentDuration { get; set; } = 2.0;

    /// <summary>
    /// Segments kept in the live playlist. Larger windows give viewers more rewind
    /// and make LL-HLS delta updates (?_HLS_skip=YES) actually skip something.
    /// </summary>
    [Range(3, 3600)] public int PlaylistWindow { get; set; } = 6;

    /// <summary>Enables LL-HLS partial segments and blocking playlist reload (fMP4 only)</summary>
    public bool LowLatency { get; set; }

    /// <summary>
    /// Re-segments SRT-ingested TS packets as-is for TS output (half the container
    /// work, byte-faithful to the source). Disable to force the demux+remux path,
    /// e.g. when MediaFrame spinner plugins must run on SRT sessions.
    /// </summary>
    public bool TsPassthrough { get; set; } = true;

    /// <summary>Target duration of LL-HLS partial segments in seconds</summary>
    [Range(0.1, 5.0)] public double PartTargetDuration { get; set; } = 0.5;
}

public abstract class MediaProtocolOptions
{
    public bool Enabled { get; set; }
}
