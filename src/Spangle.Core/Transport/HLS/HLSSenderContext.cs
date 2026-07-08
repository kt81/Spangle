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

    public HLSSenderContext(CancellationToken ct)
    {
        CancellationToken = ct;
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        Intake = pipe.Writer;
        IntakeReader = pipe.Reader;
    }

    /// <summary>
    /// Resolves the directory for the current stream, e.g. "hls-out/mystream".
    /// Must be called after media started flowing (the stream name is known by then).
    /// </summary>
    internal string ResolveStreamDirectory()
    {
        return Path.Combine(OutputDirectory, SanitizeStreamName(SourceInfo?.StreamName));
    }

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
