using System.IO.Pipelines;
using Spangle.Spinner;

namespace Spangle.Transport.HLS;

public class HLSSenderContext : ISenderContext<HLSSenderContext>, INALAnnexBSpinnerOutletAdapter
{
    public readonly CancellationToken CancellationToken;
    public PipeWriter VideoWriter { get; }
    public PipeWriter AudioWriter { get; }
    public PipeReader VideoReader { get; }
    public PipeReader AudioReader { get; }

    public HLSSenderContext(CancellationToken ct)
    {
        CancellationToken = ct;
        // TODO Abstract pipe creation
        var opt = new PipeOptions(useSynchronizationContext: false);
        var videoPipe = new Pipe(opt);
        var audioPipe = new Pipe(opt);
        VideoWriter = videoPipe.Writer;
        AudioWriter = audioPipe.Writer;
        VideoReader = videoPipe.Reader;
        AudioReader = audioPipe.Reader;
    }
}
