using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging;
using Spangle.Logging;

namespace Spangle;

public abstract class ReceiverContextBase<TSelf> : IReceiverContext<TSelf> where TSelf : ReceiverContextBase<TSelf>
{
    public string Id { get; }
    public EndPoint? RemoteEndPoint { get; init; }

    public PipeReader Reader { get; }
    public PipeWriter Writer { get; }

    public CancellationToken CancellationToken { get; set; }

    public abstract bool IsCompleted { get; }

    public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

    public static TSelf CreateInstance(string id, PipeReader reader, PipeWriter writer, CancellationToken ct = default) =>
        throw new NotImplementedException("Implement this in sub-classes.");

    protected ReceiverContextBase(string id, PipeReader reader, PipeWriter writer, CancellationToken ct)
    {
        Id = id;
        Reader = reader;
        Writer = writer;
        CancellationToken = ct;
    }

    #region Logging support

    protected static ILogger<TSelf> Logger;

    static ReceiverContextBase()
    {
        Logger = SpangleLogManager.GetLogger<TSelf>();
    }

    #endregion
}
