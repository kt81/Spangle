using Microsoft.Extensions.Logging;
using Spangle.Rtmp.Chunk;
using Spangle.Rtmp.Handshake;
using Spangle.Logging;
using Spangle.Rtmp.Chunk.Processor;
using ZLogger;

namespace Spangle.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>
{
    public string App { get; init; }
    public string StreamKey { get; init; }

    #region Headers

    // These headers are readonly but mutable
    internal BasicHeader BasicHeader = default;
    internal ChunkMessageHeader MessageHeader = default;

    #endregion

    #region State

    public ReceivingState ConnectionState = ReceivingState.HandShaking;
    internal HandshakeState HandshakeState = HandshakeState.Uninitialized;

    #endregion

    #region RPC Handlers

    internal readonly Lazy<NetConnection> NetConnection;

    #endregion

    #region Other Properties

    public uint MaxChunkSize = 128;

    public RtmpReceiverContext()
    {
        NetConnection = new Lazy<NetConnection>(() => new NetConnection(Writer));
        SetNext<BasicHeaderProcessor>();
    }

    public bool IsGoAwayEnabled { get; init; }

    #endregion

    #region For state loop

    internal delegate ValueTask Processor(RtmpReceiverContext receiverContext);

    internal Processor MoveNext { get; private set; }

    internal void SetNext<TProcessor>() where TProcessor : IChunkProcessor
    {
        MoveNext = ChunkProcessorStore<TProcessor>.Process;
        Logger.ZLogTrace("State changed: {0}", typeof(TProcessor).Name);
    }

    #endregion

    #region Utility Methods

    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    #endregion


}
