using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using Spangle.Spinner;

namespace Spangle;

public abstract class ReceiverContextBase<TSelf> : IReceiverContext, INALFileFormatSpinnerIntakeAdapter
    where TSelf : ReceiverContextBase<TSelf>
{
    public abstract string Id { get; }
    public abstract EndPoint EndPoint { get; }
    public PipeReader RemoteReader { get; }
    public PipeWriter RemoteWriter { get; }

    #region For ISpinnerIntakeAdapter
    public PipeReader VideoReader { get; }
    public PipeReader AudioReader { get; }
    internal PipeWriter VideoWriter { get; }
    internal PipeWriter AudioWriter { get; }
    #endregion

    public CancellationToken CancellationToken { get; set; }

    public abstract bool IsCompleted { get; }

    protected ReceiverContextBase(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        RemoteReader = reader;
        RemoteWriter = writer;
        CancellationToken = ct;

        // TODO Abstract pipe creation
        var opt = new PipeOptions(useSynchronizationContext: false);
        var videoPipe = new Pipe(opt);
        var audioPipe = new Pipe(opt);
        VideoReader = videoPipe.Reader;
        VideoWriter = videoPipe.Writer;
        AudioReader = audioPipe.Reader;
        AudioWriter = audioPipe.Writer;
    }

    public override string ToString()
    {
        return ZString.Format("{0}({1} from {2})", GetType().Name, Id, EndPoint.ToString());
    }

    #region Logging support

    protected static readonly ILogger<TSelf> Logger;

    static ReceiverContextBase()
    {
        Logger = SpangleLogManager.GetLogger<TSelf>();
    }

    #endregion
}
