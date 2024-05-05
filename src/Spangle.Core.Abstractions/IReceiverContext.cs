using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Spangle.Spinner;

namespace Spangle;

public interface IReceiverContext
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
    /// Video codec
    /// </summary>
    public VideoCodec? VideoCodec { get; }

    /// <summary>
    /// Audio codec
    /// </summary>
    public AudioCodec? AudioCodec { get; }

    /// <summary>
    /// Reader for remote connection
    /// </summary>
    public PipeReader RemoteReader { get; }

    /// <summary>
    /// Writer for remote connection
    /// </summary>
    public PipeWriter RemoteWriter { get; }

    /// <summary>
    /// Video outlet
    /// </summary>
    public PipeWriter? VideoOutlet { get; set; }

    /// <summary>
    /// Audio outlet
    /// </summary>
    public PipeWriter? AudioOutlet { get; set; }

    /// <summary>
    /// Cancellation token for the connection
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// The stream is completed or not
    /// </summary>
    public bool IsCompleted { get; }

    /// <summary>
    /// Event that will be fired when video codec is set
    /// </summary>
    public event Action<VideoCodec>? VideoCodecSet;

    /// <summary>
    /// Event that will be fired when audio codec is set
    /// </summary>
    public event Action<AudioCodec>? AudioCodecSet;

    /// <summary>
    /// Begin to receive data from the remote pipe (connection)
    /// </summary>
    /// <returns></returns>
    public ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource);

}
