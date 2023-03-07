using System.IO.Pipelines;
using Spangle.Protocols.Rtmp.Chunk;
using Spangle.Protocols.Rtmp.Handshake;
using Spangle.Protocols.Rtmp.NetStream;
using Spangle.Protocols.Rtmp.ReadState;
using Spangle.Util;
using ZLogger;

namespace Spangle.Protocols.Rtmp;

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

    private uint _streamIdPointer = Protocol.ControlStreamId + 1;

    /// <summary>
    /// Returns "Current" stream in receiving context.
    /// Do not call this out of the stream specific command context.
    /// </summary>
    internal RtmpNetStream? NetStream { get; private set; }

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
        if (NetStream != null)
        {
            ThrowHelper.ThrowOverSpec(this, "cannot communicate multiple streams in single connection");
        }

        uint streamId = _streamIdPointer++;
        return NetStream = new RtmpNetStream(this, streamId, streamName);
    }

    internal void RemoveStream()
    {
        NetStream = null;
    }

    internal void ReleaseStream(string streamName)
    {
        // It don't mean a thing
    }

    internal RtmpNetStream GetStreamOrError()
    {
        if (NetStream == null)
        {
            throw new InvalidOperationException("NetStream has not been created.");
        }

        return NetStream;
    }

    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    #endregion
}
