using System.IO.Pipelines;
using System.Net;

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
    /// The name of the published stream, once known (e.g. the RTMP publish name)
    /// </summary>
    public string? StreamName { get; }

    /// <summary>
    /// True when the source has declared that no video track exists (a TS program
    /// mapping only an audio stream), or when the session policy decided to treat it
    /// so (RTMP audio-only fallback). Distinguishes "audio-only by design" from
    /// "the video codec is not known yet".
    /// </summary>
    public bool IsAudioOnly { get; set; }

    /// <summary>
    /// Video dimensions from the source metadata; 0 when unknown
    /// </summary>
    public uint VideoWidth { get; }

    /// <inheritdoc cref="VideoWidth"/>
    public uint VideoHeight { get; }

    /// <summary>
    /// Reader for remote connection
    /// </summary>
    public PipeReader RemoteReader { get; }

    /// <summary>
    /// Writer for remote connection
    /// </summary>
    public PipeWriter RemoteWriter { get; }

    /// <summary>
    /// Outlet for framed media (video and audio) data.
    /// Frames are written as an in-band header followed by the payload, in arrival order.
    /// </summary>
    public PipeWriter? MediaOutlet { get; set; }

    /// <summary>
    /// Publish authorization for this session; the receiver calls it at its protocol's
    /// natural rejection point. Null when no session registry is configured (allow all).
    /// </summary>
    public IPublishGate? PublishGate { get; set; }

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
