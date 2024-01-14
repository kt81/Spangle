using System.IO.Pipelines;
using System.Net;
using Spangle.Spinner;

namespace Spangle;

public interface IReceiverContext : ISpinnerIntakeAdapter
{
    /// <summary>
    /// The identifier of the connection that will be used for logging.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Peer's endpoint
    /// </summary>
    public EndPoint EndPoint { get; }

    /// <summary>
    /// Reader for remote connection
    /// </summary>
    public PipeReader RemoteReader { get; }

    /// <summary>
    /// Writer for remote connection
    /// </summary>
    public PipeWriter RemoteWriter { get; }

    /// <summary>
    /// Cancellation token for the connection
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// The stream is completed or not
    /// </summary>
    public bool IsCompleted { get; }
}
