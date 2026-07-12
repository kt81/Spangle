using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Spangle.Transport.Rtsp.ControlFlow;
using Spangle.Transport.Rtsp.Rtp;
using ZLogger;

namespace Spangle.Transport.Rtsp;

/// <summary>
/// Pulls an RTSP stream (this server is the RTSP client) and emits canonical
/// <see cref="Spangle.Spinner.MediaFrameHeader"/> frames, exactly like the RTMP and
/// SRT receivers. Transport is TCP-interleaved by default (RTP/RTCP ride the RTSP
/// connection — no extra sockets, no firewall holes) or UDP (RTP/RTCP on their own
/// sockets, resequenced on arrival). One connection = one <see cref="RtspReceiverContext"/>.
/// </summary>
public sealed class RtspReceiverContext : ReceiverContextBase<RtspReceiverContext>
{
    private readonly string _url;
    private readonly RtspAuthenticator _authenticator;
    private readonly RtspDialect _dialect;
    private readonly EndPoint _endPoint;
    private readonly RtspTransportMode _transport;

    public override string Id { get; }
    public override EndPoint EndPoint => _endPoint;
    public override string? StreamName { get; }

    private bool _completed;
    public override bool IsCompleted => _completed;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "The camera URL comes verbatim from configuration and is passed straight to the wire; parsing it to Uri would lose credentials and query quirks cameras rely on")]
    public RtspReceiverContext(PipeReader reader, PipeWriter writer, string url, string streamName,
        EndPoint endPoint, string? userName, string? password, RtspDialect dialect, CancellationToken ct,
        RtspTransportMode transport = RtspTransportMode.Tcp)
        : base(reader, writer, ct)
    {
        _url = url;
        StreamName = streamName;
        _endPoint = endPoint;
        _authenticator = new RtspAuthenticator(userName, password);
        _dialect = dialect;
        _transport = transport;
        Id = ZString.Format("RTSP_{0}", streamName);
    }

    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        // publish authorization (and same-name takeover) before any media is consumed
        if (PublishGate is { } gate && !await gate.TryOpenAsync(StreamName ?? Id, CancellationToken).ConfigureAwait(false))
        {
            Logger.ZLogInformation($"RTSP publish rejected: {Id}");
            return;
        }

        using var connection = new RtspConnection(RemoteReader, RemoteWriter);
        var adapter = new RtspMediaFrameAdapter<RtspReceiverContext>(this);
        var flow = new RtspControlFlow(connection, _url, _authenticator, _dialect, adapter, _transport);

        connection.OnInterleaved = (channel, payload) => OnInterleavedAsync(flow, adapter, channel, payload);

        // the read loop must run while the handshake is in flight: it dispatches the
        // responses the handshake awaits (and answers server keepalive probes)
        Task<long> readLoop = connection.RunReadLoopAsync(CancellationToken).AsTask();
        var udpLoops = new List<Task>();

        try
        {
            await flow.RunAsync(CancellationToken).ConfigureAwait(false);
            Logger.ZLogInformation($"RTSP session established: {Id} ({_url}, {_transport})");

            // UDP transport: RTP/RTCP arrive on their own sockets, not the RTSP connection
            foreach (UdpTrackBinding binding in flow.UdpBindings)
            {
                udpLoops.Add(await StartUdpTrackAsync(adapter, binding).ConfigureAwait(false));
            }

            using var keepAlive = StartKeepAlive(flow);
            await readLoop.ConfigureAwait(false); // media flows until the peer or we stop
        }
        finally
        {
            _completed = true;
            await flow.TeardownAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (UdpTrackBinding binding in flow.UdpBindings)
            {
                binding.Dispose(); // closes the sockets, ending the receive loops
            }
            await Task.WhenAll(udpLoops).ConfigureAwait(false);

            // drain: emit the frames already assembled before the pipeline finalizes
            if (MediaOutlet is not null && adapter.HasPendingFrames)
            {
                adapter.HasPendingFrames = false;
                await MediaOutlet.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Logger.ZLogInformation($"RTSP session ended: {Id}");
        }
    }

    /// <summary>
    /// Starts the RTP and RTCP receive loops for one UDP track, after a NAT-punch datagram
    /// opens the return path. RTP goes through a reorder buffer (UDP can deliver out of
    /// order); RTCP is dispatched directly. Returns a task that completes when both loops end.
    /// </summary>
    private async ValueTask<Task> StartUdpTrackAsync(RtspMediaFrameAdapter<RtspReceiverContext> adapter,
        UdpTrackBinding binding)
    {
        await NatPunchAsync(binding.Sockets.Rtp, binding.ServerRtp).ConfigureAwait(false);
        await NatPunchAsync(binding.Sockets.Rtcp, binding.ServerRtcp).ConfigureAwait(false);

        var reorder = new RtpReorderBuffer(windowSize: 128,
            (buffer, length) => RtpDatagramDispatch.Dispatch(adapter, binding.Kind, isRtcp: false,
                new ReadOnlySpan<byte>(buffer, 0, length)));

        Task rtp = UdpMediaSocketPair.ReceiveLoopAsync(binding.Sockets.Rtp, async datagram =>
        {
            AddBytesReceived(datagram.Length);
            reorder.Add(datagram.Span);
            await FlushPendingAsync(adapter).ConfigureAwait(false);
        }, CancellationToken);

        Task rtcp = UdpMediaSocketPair.ReceiveLoopAsync(binding.Sockets.Rtcp, async datagram =>
        {
            RtpDatagramDispatch.Dispatch(adapter, binding.Kind, isRtcp: true, datagram.Span);
            await FlushPendingAsync(adapter).ConfigureAwait(false);
        }, CancellationToken);

        return Task.WhenAll(rtp, rtcp);
    }

    private async ValueTask NatPunchAsync(System.Net.Sockets.Socket socket, System.Net.IPEndPoint destination)
    {
        try
        {
            // an empty datagram opens the path through a NAT/firewall to the camera
            await socket.SendToAsync(ReadOnlyMemory<byte>.Empty, System.Net.Sockets.SocketFlags.None,
                destination, CancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // best effort; on a direct LAN the punch is unnecessary anyway
        }
    }

    private async ValueTask FlushPendingAsync(RtspMediaFrameAdapter<RtspReceiverContext> adapter)
    {
        if (MediaOutlet is not null && adapter.HasPendingFrames)
        {
            adapter.HasPendingFrames = false;
            await MediaOutlet.FlushAsync(CancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnInterleavedAsync(RtspControlFlow flow, RtspMediaFrameAdapter<RtspReceiverContext> adapter,
        int channel, ReadOnlySequence<byte> payload)
    {
        if (!flow.Channels.TryGetValue(channel, out RtspControlFlow.TrackChannel? track))
        {
            return; // a channel we never set up
        }

        AddBytesReceived(payload.Length);
        // interleaved payloads are small (≤ 64 KiB by the length field); a pooled
        // contiguous copy lets the parsers work on a span
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

    private CancellationTokenSource StartKeepAlive(RtspControlFlow flow)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(flow.KeepAliveInterval);
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    await flow.SendKeepAliveAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // session ending
            }
            catch (Exception e)
            {
                Logger.ZLogWarning($"RTSP keepalive stopped: {e.Message}");
            }
        }, cts.Token);
        return cts;
    }
}
