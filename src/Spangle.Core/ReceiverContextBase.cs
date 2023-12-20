using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Spangle.Logging;

namespace Spangle;

public abstract class ReceiverContextBase<TSelf> : IReceiverContext
    where TSelf : ReceiverContextBase<TSelf>
{
    public abstract string Id { get; }
    public abstract EndPoint EndPoint { get; }

    public PipeReader Reader { get; }
    public PipeWriter Writer { get; }

    public CancellationToken CancellationToken { get; set; }

    public abstract bool IsCompleted { get; }

    protected ReceiverContextBase(PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        Reader = reader;
        Writer = writer;
        CancellationToken = ct;
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
