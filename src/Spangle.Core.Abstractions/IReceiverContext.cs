using System.IO.Pipelines;
using System.Net;

namespace Spangle;

public interface IReceiverContext
{
    /// <summary>
    /// The identifier of the connection that will be used for logging.
    /// </summary>
    public string Id { get; }
    public EndPoint EndPoint { get; }

    public PipeReader Reader { get; }
    public PipeWriter Writer { get; }

    public CancellationToken CancellationToken { get; set; }

    public bool IsCompleted { get; }
}
