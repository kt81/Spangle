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
