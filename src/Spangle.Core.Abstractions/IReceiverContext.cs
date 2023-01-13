using System.IO.Pipelines;
using System.Net;

namespace Spangle;

public interface IReceiverContext
{
    /// <summary>
    /// The identifier of the connection that will be used for logging.
    /// </summary>
    public string Id { get; init; }
    public EndPoint? RemoteEndPoint { get; init; }

    public PipeReader Reader { get; init; }
    public PipeWriter Writer { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public bool IsCompleted { get; }
}
