using System.IO.Pipelines;

namespace Spangle.Transport.HLS;

public enum HLSSegmentFormat
{
    /// <summary>MPEG-2 TS segments (.ts). The intake carries a muxed TS stream from a spinner.</summary>
    MpegTs,

    /// <summary>CMAF/fMP4 segments (.m4s + init.mp4). The intake carries MediaFrame records directly.</summary>
    Fmp4,
}

public class HLSSenderContext : ISenderContext<HLSSenderContext>
{
    public readonly CancellationToken CancellationToken;

    public PipeWriter Intake { get; }
    public PipeReader IntakeReader { get; }

    public IReceiverContext? SourceInfo { get; set; }

    /// <summary>Root directory; each stream writes into a subdirectory named after the stream</summary>
    public string OutputDirectory { get; set; } = "hls-out";

    /// <summary>Minimum segment duration in seconds; segments are cut at the first keyframe after this</summary>
    public double TargetSegmentDuration { get; set; } = 2.0;

    public HLSSegmentFormat SegmentFormat { get; set; } = HLSSegmentFormat.MpegTs;

    /// <summary>Enables LL-HLS partial segments (fMP4 only)</summary>
    public bool LowLatency { get; set; }

    /// <summary>Target duration of LL-HLS partial segments in seconds</summary>
    public double PartTargetDuration { get; set; } = 0.5;

    /// <summary>When set, playlist updates are published here for in-memory serving and blocking reload</summary>
    public HLSStreamRegistry? Registry { get; set; }

    public HLSSenderContext(CancellationToken ct)
    {
        CancellationToken = ct;
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        Intake = pipe.Writer;
        IntakeReader = pipe.Reader;
    }

    /// <summary>
    /// The sanitized stream name used as the directory name and the registry key.
    /// Must be called after media started flowing (the stream name is known by then).
    /// </summary>
    internal string ResolveStreamKey() => SanitizeStreamName(SourceInfo?.StreamName);

    /// <inheritdoc cref="ResolveStreamKey"/>
    internal string ResolveStreamDirectory() => Path.Combine(OutputDirectory, ResolveStreamKey());

    private static string SanitizeStreamName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "stream";
        }
        Span<char> buff = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buff[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return new string(buff);
    }
}
