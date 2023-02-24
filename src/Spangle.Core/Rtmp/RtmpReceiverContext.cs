using System.IO.Pipelines;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;
using Spangle.Rtmp.NetStream;
using Spangle.Rtmp.ReadState;
using ZLogger;

namespace Spangle.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>,
    IReceiverContext<RtmpReceiverContext>
{
    #region Headers

    // =======================================================================

    internal BasicHeader   BasicHeader   = default;
    internal MessageHeader MessageHeader = default;

    internal BasicHeader   BasicHeaderToSend   = default;
    internal MessageHeader MessageHeaderToSend = default;

    /// <summary>
    /// Previous message format for next Fmt3
    /// </summary>
    internal MessageHeaderFormat PreviousFormat = MessageHeaderFormat.Fmt0;

    #endregion

    #region Settings & Protocol Info

    // =======================================================================

    // TODO 設定とかからもらう
    public uint Bandwidth      = 1500000;
    public uint ChunkSize      = Protocol.MinChunkSize;
    public uint MaxMessageSize = Protocol.MaxMessageSizeDefault;

    /// <summary>
    /// Timeout milliseconds.
    /// It is checked for every State actions.
    /// </summary>
    public int Timeout = 0;

    public string? App;
    public string? PreparingStreamName;

    public bool IsGoAwayEnabled;

    #endregion

    #region State

    // =======================================================================

    public   uint           Timestamp       = 0;
    public   ReceivingState ConnectionState = ReceivingState.HandShaking;
    internal HandshakeState HandshakeState  = HandshakeState.Uninitialized;

    private uint                            StreamIdPointer = Protocol.ControlStreamId + 1;
    private Dictionary<uint, RtmpNetStream> _netStreams     = new();

    /// <summary>
    /// Returns "Current" stream in receiving context.
    /// Do not call this out of the stream specific command context.
    /// </summary>
    internal RtmpNetStream NetStream
    {
        get { return _netStreams[MessageHeader.StreamId]; }
    }

    #endregion

    #region For state loop

    // =======================================================================

    internal IReadStateAction.Action MoveNext { get; private set; }
        = StateStore<ReadChunkHeader>.Action;

    internal void SetNext<TProcessor>() where TProcessor : IReadStateAction
    {
        MoveNext = StateStore<TProcessor>.Action;
        Logger.ZLogTrace("State changed: {0}", typeof(TProcessor).Name);
    }

    #endregion

    #region ctor

    // =======================================================================
    private RtmpReceiverContext(string id, PipeReader reader, PipeWriter writer, CancellationToken ct) : base(id,
        reader, writer, ct)
    {
    }

    public static new RtmpReceiverContext CreateInstance(string id, PipeReader reader, PipeWriter writer,
        CancellationToken ct = default)
    {
        return new RtmpReceiverContext(id, reader, writer, ct);
    }

    #endregion

    #region Utility Methods

    // =======================================================================

    internal RtmpNetStream CreateStream(string streamName)
    {
        uint streamId = StreamIdPointer++;
        return _netStreams[streamId] = new RtmpNetStream(this, streamId, streamName);
    }

    internal void ReleaseStream(string streamName)
    {
    }

    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    #endregion
}
