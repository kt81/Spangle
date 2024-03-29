﻿using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Spangle.Spinner;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.Handshake;
using Spangle.Transport.Rtmp.NetStream;
using Spangle.Transport.Rtmp.ReadState;
using Spangle.Util;
using ZLogger;

namespace Spangle.Transport.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>, INALFileFormatSpinnerIntakeAdapter
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

    public VideoCodec VideoCodec = VideoCodec.H264;
    public AudioCodec AudioCodec = AudioCodec.AAC;

    /// <summary>
    /// Enhanced RTMP mode.
    /// </summary>
    public bool IsEnhanced = false;

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

    public override string Id { get; }
    public override EndPoint EndPoint { get; }
    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    private uint _streamIdPointer = Protocol.ControlStreamId + 1;

    /// <summary>
    /// Returns "Current" stream in receiving context.
    /// Do not call this out of the stream specific command context.
    /// </summary>
    internal RtmpNetStream? NetStream { get; private set; }

    #endregion

    #region IO

    // ChannelWriter<FlvPacket>

    #endregion

    #region For state loop

    // =======================================================================

    internal IReadStateAction.Action MoveNext { get; private set; }
        = StateStore<ReadChunkHeader>.Action;

    internal void SetNext<TProcessor>() where TProcessor : IReadStateAction
    {
        MoveNext = StateStore<TProcessor>.Action;
        Logger.ZLogTrace($"State changed: {typeof(TProcessor).Name}");
    }

    #endregion

    #region ctor

    // =======================================================================

    public RtmpReceiverContext(PipeReader reader, PipeWriter writer, EndPoint remoteEndPoint, CancellationToken ct,
        string? id = null) : base(reader, writer, ct)
    {
        EndPoint = remoteEndPoint;
        Id = id ?? Guid.NewGuid().ToString();
    }

    public static RtmpReceiverContext CreateFromTcpClient(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        if (!stream.CanRead)
        {
            throw new ArgumentException("TCPClient must be able to read", nameof(client));
        }

        if (!stream.CanWrite)
        {
            throw new ArgumentException("TCPClient must be able to write", nameof(client));
        }

        return new RtmpReceiverContext(
            PipeReader.Create(stream), PipeWriter.Create(stream),
            client.Client.LocalEndPoint!, ct);
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

    #endregion
}
