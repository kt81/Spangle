using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Spangle.Transport.Rtsp.Rtp;
using ZLogger;

namespace Spangle.Transport.Rtsp.Server;

/// <summary>
/// Accepts an RTSP push (this server is the RTSP server; a client RECORDs to it) and
/// emits canonical <see cref="Spangle.Spinner.MediaFrameHeader"/> frames, exactly like
/// the other receivers. Transport is whatever the client's SETUP asks for —
/// TCP-interleaved or UDP (server ports bound per track). The publish target is the URL
/// path of the ANNOUNCE, so unlike the pull receiver this fits the RTMP/SRT publish model:
/// one accepted connection = one <see cref="RtspPushReceiverContext"/>, authorized
/// through <see cref="IPublishGate"/> with same-name takeover.
/// </summary>
public sealed class RtspPushReceiverContext : ReceiverContextBase<RtspPushReceiverContext>
{
    private readonly EndPoint _endPoint;
    private readonly string _id;
    private volatile string? _streamName;
    private bool _completed;

    public override string Id => _id;
    public override EndPoint EndPoint => _endPoint;
    public override string? StreamName => _streamName;
    public override bool IsCompleted => _completed;

    public RtspPushReceiverContext(PipeReader reader, PipeWriter writer, EndPoint endPoint, CancellationToken ct)
        : base(reader, writer, ct)
    {
        _endPoint = endPoint;
        _id = ZString.Format("RTSPPUSH_{0}", endPoint);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks complete before disposal",
        Justification = "The UDP receive loops are stopped via udpStop.Cancel() and awaited to completion in the finally before the sockets and the CTS are disposed")]
    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        using var connection = new RtspServerConnection(RemoteReader, RemoteWriter);
        var adapter = new RtspMediaFrameAdapter<RtspPushReceiverContext>(this);
        var flow = new RtspServerControlFlow(adapter);
        var gateOpened = false;
        var udpLoops = new List<Task>();
        using var udpStop = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        connection.OnRequest = async request =>
        {
            // authorize the publish before ANNOUNCE wires the pipeline; the stream name
            // is the URL path, known from the request line before the SDP is parsed
            if (!gateOpened && request.Method.Equals("ANNOUNCE", StringComparison.OrdinalIgnoreCase))
            {
                string name = RtspServerControlFlow.ExtractStreamName(request.Uri);
                _streamName = name;
                if (PublishGate is { } gate && !await gate.TryOpenAsync(name, CancellationToken).ConfigureAwait(false))
                {
                    Logger.ZLogInformation($"RTSP push rejected: {name} from {_endPoint}");
                    _completed = true;
                    return RtspResponse.Status(401, "Unauthorized");
                }
                gateOpened = true;
            }

            RtspResponse response = await flow.HandleAsync(request).ConfigureAwait(false);
            if (flow.StreamName is { } sn)
            {
                _streamName = sn;
            }
            // RECORD: UDP tracks (if any) start delivering to the ports SETUP bound
            if (flow.Recording && udpLoops.Count == 0 && flow.UdpTracks.Count > 0)
            {
                foreach (UdpServerTrack track in flow.UdpTracks)
                {
                    udpLoops.Add(StartUdpTrackAsync(adapter, track, udpStop.Token));
                }
            }
            if (flow.TornDown)
            {
                _completed = true;
                await readTimeoutSource.CancelAsync().ConfigureAwait(false); // end the read loop after this response
            }
            return response;
        };

        connection.OnInterleaved = (channel, payload) => OnInterleavedAsync(flow, adapter, channel, payload);

        try
        {
            Logger.ZLogInformation($"RTSP push connection opened: {_endPoint}");
            await connection.RunAsync(CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _completed = true;
            await udpStop.CancelAsync().ConfigureAwait(false); // stop the receive loops first
            await Task.WhenAll(udpLoops).ConfigureAwait(false);
            foreach (UdpServerTrack track in flow.UdpTracks)
            {
                track.Dispose(); // then close the sockets
            }
            if (MediaOutlet is not null && adapter.HasPendingFrames)
            {
                adapter.HasPendingFrames = false;
                await MediaOutlet.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Logger.ZLogInformation($"RTSP push connection closed: {_id}");
        }
    }

    /// <summary>Runs the RTP (reordered) and RTCP receive loops for one UDP push track.</summary>
    private Task StartUdpTrackAsync(RtspMediaFrameAdapter<RtspPushReceiverContext> adapter, UdpServerTrack track,
        CancellationToken ct)
    {
        var reorder = new RtpReorderBuffer(windowSize: 128,
            (buffer, length) => RtpDatagramDispatch.Dispatch(adapter, track.Kind, isRtcp: false,
                new ReadOnlySpan<byte>(buffer, 0, length)));

        Task rtp = UdpMediaSocketPair.ReceiveLoopAsync(track.Sockets.Rtp, async datagram =>
        {
            AddBytesReceived(datagram.Length);
            reorder.Add(datagram.Span);
            await FlushPendingAsync(adapter).ConfigureAwait(false);
        }, ct);

        Task rtcp = UdpMediaSocketPair.ReceiveLoopAsync(track.Sockets.Rtcp, async datagram =>
        {
            RtpDatagramDispatch.Dispatch(adapter, track.Kind, isRtcp: true, datagram.Span);
            await FlushPendingAsync(adapter).ConfigureAwait(false);
        }, ct);

        return Task.WhenAll(rtp, rtcp);
    }

    private async ValueTask FlushPendingAsync(RtspMediaFrameAdapter<RtspPushReceiverContext> adapter)
    {
        if (MediaOutlet is not null && adapter.HasPendingFrames)
        {
            adapter.HasPendingFrames = false;
            await MediaOutlet.FlushAsync(CancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnInterleavedAsync(RtspServerControlFlow flow,
        RtspMediaFrameAdapter<RtspPushReceiverContext> adapter, int channel, ReadOnlySequence<byte> payload)
    {
        if (!flow.Channels.TryGetValue(channel, out RtspServerControlFlow.TrackChannel? track))
        {
            return;
        }

        AddBytesReceived(payload.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
        try
        {
            payload.CopyTo(rented);
            RtpDatagramDispatch.Dispatch(adapter, track.Kind, channel == track.RtcpChannel,
                new ReadOnlySpan<byte>(rented, 0, (int)payload.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (MediaOutlet is not null && adapter.HasPendingFrames)
        {
            adapter.HasPendingFrames = false;
            await MediaOutlet.FlushAsync(CancellationToken).ConfigureAwait(false);
        }
    }
}
