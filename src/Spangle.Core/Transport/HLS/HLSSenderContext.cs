using System.Buffers;
using System.IO.Pipelines;
using Spangle.Spinner;

namespace Spangle.Transport.HLS;

public class HLSSenderContext : ISenderContext<HLSSenderContext>
{
    public readonly CancellationToken CancellationToken;
    public PipeWriter VideoIntake { get; set; }
    public PipeWriter AudioIntake { get; set; }
    public VideoCodec VideoCodec { get; set; }
    public AudioCodec AudioCodec { get; set; }
    public PipeReader VideoReader { get; }
    public PipeReader AudioReader { get; }

    /// <summary>Directory where segments and the playlist are written.</summary>
    public string OutputDirectory { get; set; } = "hls-out";

    /// <summary>Minimum segment duration in seconds; segments are cut at the first keyframe after this.</summary>
    public double TargetSegmentDuration { get; set; } = 2.0;

    public HLSSenderContext(CancellationToken ct)
    {
        CancellationToken = ct;
        // TODO Abstract pipe creation
        var opt = new PipeOptions(useSynchronizationContext: false);
        var videoPipe = new Pipe(opt);
        var audioPipe = new Pipe(opt);
        VideoIntake = videoPipe.Writer;
        AudioIntake = audioPipe.Writer;
        VideoReader = videoPipe.Reader;
        AudioReader = audioPipe.Reader;
    }
}
