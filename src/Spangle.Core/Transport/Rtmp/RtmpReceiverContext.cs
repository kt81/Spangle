using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Transport.Rtmp.Chunk;
using Spangle.Transport.Rtmp.Handshake;
using Spangle.Transport.Rtmp.NetStream;
using Spangle.Transport.Rtmp.ProtocolControlMessage;
using Spangle.Transport.Rtmp.ReadState;
using Spangle.Util;
using ZLogger;

namespace Spangle.Transport.Rtmp;

public sealed class RtmpReceiverContext : ReceiverContextBase<RtmpReceiverContext>
{
    private static readonly ILogger<RtmpReceiverContext> s_logger = SpangleLogManager.GetLogger<RtmpReceiverContext>();

    #region Headers

    // =======================================================================

    internal BasicHeader   BasicHeader;
    internal MessageHeader MessageHeader;

    internal BasicHeader   BasicHeaderToSend;
    internal MessageHeader MessageHeaderToSend;

    #endregion

    #region Settings & Protocol Info

    // =======================================================================

    /// <summary>
    /// Announced to the peer as WindowAcknowledgementSize / SetPeerBandwidth during
    /// the connect sequence. Hosts override this from configuration (Rtmp.Bandwidth).
    /// </summary>
    public uint Bandwidth      = 1500000;

    /// <summary>
    /// Chunk size for incoming chunks. Updated by the SetChunkSize message from the peer.
    /// </summary>
    public uint ChunkSize      = Protocol.MinChunkSize;

    /// <summary>
    /// Chunk size for outgoing chunks. Announced to the peer by our own SetChunkSize message.
    /// </summary>
    public uint SendChunkSize  = Protocol.MinChunkSize;

    public uint MaxMessageSize = Protocol.MaxMessageSizeDefault;

    /// <summary>
    /// Enhanced RTMP mode.
    /// </summary>
    public bool IsEnhanced;

    /// <summary>
    /// Timeout milliseconds.
    /// It is checked for every State actions.
    /// </summary>
    public int Timeout;

    public string? App;
    public string? PreparingStreamName;

    /// <inheritdoc/>
    public override string? StreamName => PreparingStreamName;

    public bool IsGoAwayEnabled;

    #endregion

    #region State

    // =======================================================================

    /// <summary>The raw 32-bit millisecond timestamp of the current message (wraps every 49.7 days).</summary>
    public uint Timestamp;

    /// <summary>
    /// The current message's decode timestamp on the canonical 90 kHz tick timeline, unwrapped from
    /// <see cref="Timestamp"/> so a long-running stream does not reset when the 32-bit field wraps.
    /// </summary>
    public long TimestampTicks { get; private set; }

    private RtmpTimestampUnwrapper _timestampUnwrapper;

    public   ReceivingState ConnectionState = ReceivingState.HandShaking;
    internal HandshakeState HandshakeState  = HandshakeState.Uninitialized;

    public override string Id { get; }
    public override EndPoint EndPoint { get; }
    public override bool IsCompleted => ConnectionState == ReceivingState.Terminated;

    private uint _streamIdPointer = Protocol.ControlStreamId + 1;

    /// <summary>
    /// The protocol allows 65,599 chunk stream ids, each holding an assembly buffer
    /// that never shrinks; real clients use a handful, so cap it to bound memory.
    /// </summary>
    private const int MaxChunkStreams = 64;

    private readonly Dictionary<uint, ChunkStreamState> _chunkStreams = new();

    internal ChunkStreamState GetChunkStreamState(uint chunkStreamId)
    {
        if (_chunkStreams.TryGetValue(chunkStreamId, out var state))
        {
            return state;
        }
        if (_chunkStreams.Count >= MaxChunkStreams)
        {
            throw new InvalidDataException($"Too many chunk streams (limit {MaxChunkStreams})");
        }
        state = new ChunkStreamState(chunkStreamId);
        _chunkStreams.Add(chunkStreamId, state);
        return state;
    }

    /// <summary>
    /// Adopts a newly completed message's 32-bit millisecond timestamp as the current one, updating
    /// both the raw value and its unwrapped 90 kHz tick projection.
    /// </summary>
    public void SetTimestamp(uint milliseconds)
    {
        Timestamp = milliseconds;
        TimestampTicks = _timestampUnwrapper.Unwrap(milliseconds) * 90;
    }

    /// <summary>
    /// Unwraps RTMP's 32-bit millisecond timestamps into a monotonic 64-bit timeline. RTMP interleaves
    /// audio, video and data on one timeline, so the sequence dips frame to frame; only a jump larger
    /// than half the 32-bit range counts as a wrap, and the reverse correction untangles the two tracks
    /// that straddle the wrap boundary (the same extension <see cref="Rtsp.Rtp.RtpTimeline"/> applies).
    /// </summary>
    private struct RtmpTimestampUnwrapper
    {
        private long _epochs;
        private uint _lastRaw;
        private bool _hasLast;

        public long Unwrap(uint raw)
        {
            if (_hasLast)
            {
                if (raw < _lastRaw && _lastRaw - raw > uint.MaxValue / 2)
                {
                    _epochs++;
                }
                else if (raw > _lastRaw && raw - _lastRaw > uint.MaxValue / 2 && _epochs > 0)
                {
                    _epochs--; // an out-of-order timestamp from just before a wrap
                }
            }
            _lastRaw = raw;
            _hasLast = true;
            return (_epochs << 32) | raw;
        }
    }

    // =======================================================================
    // Acknowledgement (RTMP 5.4.3 / 5.4.4)

    private uint _peerAckWindowSize; // 0 until the peer sends a Window Acknowledgement Size
    private long _bytesAtLastAck;

    /// <summary>Records the peer's Window Acknowledgement Size: we acknowledge every this-many received bytes.</summary>
    public void SetPeerAckWindowSize(uint windowSize) => _peerAckWindowSize = windowSize;

    /// <summary>
    /// Sends an Acknowledgement (RTMP 5.4.3) once the peer has advertised a window and a window's
    /// worth of bytes has arrived since the last one. The sequence number is the running received
    /// byte count (32-bit, wrapping as the spec intends). A no-op until the peer asks for acks.
    /// </summary>
    public async ValueTask MaybeAcknowledgeAsync()
    {
        if (_peerAckWindowSize == 0)
        {
            return;
        }
        long received = BytesReceived;
        if (received - _bytesAtLastAck < _peerAckWindowSize)
        {
            return;
        }
        _bytesAtLastAck = received;

        var sequenceNumber = BigEndianUInt32.FromHost((uint)received);
        RtmpWriter.Write(this, 0, MessageType.Acknowledgement,
            Protocol.ControlChunkStreamId, Protocol.ControlStreamId, ref sequenceNumber);
        await RemoteWriter.FlushAsync(CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns "Current" stream in receiving context.
    /// Do not call this out of the stream specific command context.
    /// </summary>
    internal RtmpNetStream? NetStream { get; private set; }

    /// <summary>
    /// An audio codec config (AAC sequence header / OpusHead) that arrived before the
    /// pipeline was wired (the wiring triggers on the video codec); replayed into the
    /// media outlet ahead of the next audio frame.
    /// </summary>
    internal byte[]? PendingAudioConfig;

    /// <inheritdoc cref="PendingAudioConfig"/>
    internal AudioCodec PendingAudioConfigCodec;

    /// <summary>Keeps unsupported-audio warnings to one line per session.</summary>
    internal bool AudioUnsupportedLogged;

    /// <summary>Keeps the video-after-audio-only warning to one line per session.</summary>
    internal bool VideoAfterAudioOnlyLogged;

    #endregion

    #region IO



    #endregion

    #region Receive loop

    // =======================================================================

    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        s_logger.ZLogDebug($"Begin to handshake");
        await HandshakeHandler.DoHandshakeAsync(this).ConfigureAwait(false);
        s_logger.ZLogDebug($"Handshake done");
        ConnectionState = ReceivingState.WaitingConnect;

        // One iteration = one chunk. Complete messages are dispatched inside.
        while (!IsCompleted)
        {
            if (Timeout > 0)
            {
                readTimeoutSource.CancelAfter(Timeout);
                await ReadChunkHeader.PerformAsync(this).ConfigureAwait(false);
                readTimeoutSource.TryReset();
            }
            else
            {
                await ReadChunkHeader.PerformAsync(this).ConfigureAwait(false);
            }
        }

        readTimeoutSource.TryReset();

        s_logger.ZLogInformation($"Rtmp connection closed");
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
            client.Client.RemoteEndPoint!, ct);
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

    internal static void ReleaseStream(string streamName)
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
