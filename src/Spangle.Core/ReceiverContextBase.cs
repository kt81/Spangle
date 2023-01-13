using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Logging;

namespace Spangle;

public abstract class ReceiverContextBase<TSelf> : IReceiverContext where TSelf : ReceiverContextBase<TSelf>
{
    public string Id { get; init; }
    public EndPoint? RemoteEndPoint { get; init; }

    public PipeReader Reader { get; init; }
    public PipeWriter Writer { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public abstract bool IsCompleted { get; }

    public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

    #region Logging support
    protected static ILogger<TSelf> Logger;
    static ReceiverContextBase()
    {
        Logger = SpangleLogManager.GetLogger<TSelf>();
    }
    #endregion
}
