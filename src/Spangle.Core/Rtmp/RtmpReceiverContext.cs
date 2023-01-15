using System.IO.Pipelines;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;
using Spangle.Rtmp.ReadState;
using ZLogger;

namespace Spangle.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>, IReceiverContext<RtmpReceiverContext>
{
    #region Headers
    // =======================================================================

    internal BasicHeader   BasicHeader   = default;
    internal MessageHeader MessageHeader = default;

    internal BasicHeader   BasicHeaderToSend   = default;
    internal MessageHeader MessageHeaderToSend = default;

    #endregion

    #region Settings & Protocol Info
    // =======================================================================

    // TODO 設定とかからもらう
    public uint Bandwidth    = 1500000;
    public uint MaxChunkSize = 128;

    /// <summary>
    /// Timeout millisecond.
    /// It is checked for every State actions.
    /// </summary>
    public int Timeout  = 5000;

    public string? App;
    public string? StreamKey;

    public bool IsGoAwayEnabled;

    #endregion

    #region State
    // =======================================================================

    public   uint           Timestamp       = 0;
    public   ReceivingState ConnectionState = ReceivingState.HandShaking;
    internal HandshakeState HandshakeState  = HandshakeState.Uninitialized;

    #endregion

    #region For state loop
    // =======================================================================

    internal IReadStateAction.Action MoveNext { get; private set; }
        = StateStore<ReadBasicHeader>.Action;

    internal void SetNext<TProcessor>() where TProcessor : IReadStateAction
    {
        MoveNext = StateStore<TProcessor>.Action;
        Logger.ZLogTrace("State changed: {0}", typeof(TProcessor).Name);
    }

    #endregion

    #region ctor
    // =======================================================================
    private RtmpReceiverContext(string id, PipeReader reader, PipeWriter writer, CancellationToken ct) : base(id, reader, writer, ct)
    {
    }

    public static new RtmpReceiverContext CreateInstance(string id, PipeReader reader, PipeWriter writer, CancellationToken ct = default)
    {
        return new RtmpReceiverContext(id, reader, writer, ct);
    }

    #endregion

    #region Utility Methods
    // =======================================================================

    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    #endregion
}
