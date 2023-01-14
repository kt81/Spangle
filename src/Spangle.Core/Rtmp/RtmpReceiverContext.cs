using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;
using Spangle.Rtmp.ReadState;
using ZLogger;

namespace Spangle.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>
{
    public string App { get; init; }
    public string StreamKey { get; init; }

    // TODO 設定とかからもらう
    public uint Bandwidth = 1500000;

    #region Headers

    // These headers are readonly but mutable
    internal BasicHeader BasicHeader = default;
    internal ChunkMessageHeader MessageHeader = default;

    #endregion

    #region State

    public ReceivingState ConnectionState = ReceivingState.HandShaking;
    internal HandshakeState HandshakeState = HandshakeState.Uninitialized;

    #endregion

    #region Other Properties

    public uint MaxChunkSize = 128;

    public RtmpReceiverContext()
    {
        SetNext<ReadBasicHeader>();
    }

    public bool IsGoAwayEnabled { get; init; }

    #endregion

    #region For state loop


    internal IReadStateAction.Action MoveNext { get; private set; }

    internal void SetNext<TProcessor>() where TProcessor : IReadStateAction
    {
        MoveNext = StateStore<TProcessor>.Action;
        Logger.ZLogTrace("State changed: {0}", typeof(TProcessor).Name);
    }

    #endregion

    #region Utility Methods

    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    #endregion


}
