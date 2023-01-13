using System.IO.Pipelines;
using System.Net;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;

namespace Spangle.Rtmp;


public class RtmpReceiverContext : IReceiverContext
{
    /// <summary>
    /// The identifier of the connection that will be used for logging.
    /// </summary>
    public string Id { get; init; }

    public string App;
    public string StreamKey;

    #region IO

    public EndPoint RemoteEndPoint { get; init; }
    public PipeReader Reader { get; init; }
    public PipeWriter Writer { get; init; }

    #endregion

    #region Headers

    // These headers are readonly but mutable
    internal readonly BasicHeader BasicHeader = new();
    internal readonly ChunkMessageHeader MessageHeader = new();

    #endregion

    #region State

    public CancellationToken CancellationToken { get; init; }
    public ReceivingState ReceivingState = ReceivingState.Established;
    internal HandshakeState HandshakeState = HandshakeState.Uninitialized;

    public bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

    #endregion

    #region Other Properties

    public int MaxChunkSize = 128;
    public bool IsGoAwayEnabled { get; init; }

    #endregion

    public delegate ValueTask ChunkReader(RtmpReceiverContext receiverContext, CancellationToken ct);

    #region Utility Methods

    public void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();

    #endregion
}
