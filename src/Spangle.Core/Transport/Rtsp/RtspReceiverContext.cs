using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Spangle.Transport.Rtsp.ControlFlow;
using Spangle.Transport.Rtsp.Rtp;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp;

/// <summary>
/// Pulls an RTSP stream (this server is the RTSP client) and emits canonical
/// <see cref="Spangle.Spinner.MediaFrameHeader"/> frames, exactly like the RTMP and
/// SRT receivers. TCP-interleaved transport only (the roadmap's first target): RTP
/// and RTCP ride the RTSP connection itself, so there are no extra sockets and no
/// firewall holes to punch. One connection = one <see cref="RtspReceiverContext"/>.
/// </summary>
public sealed class RtspReceiverContext : ReceiverContextBase<RtspReceiverContext>
{
    private readonly string _url;
    private readonly RtspAuthenticator _authenticator;
    private readonly RtspDialect _dialect;
    private readonly EndPoint _endPoint;

    public override string Id { get; }
    public override EndPoint EndPoint => _endPoint;
    public override string? StreamName { get; }

    private bool _completed;
    public override bool IsCompleted => _completed;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "The camera URL comes verbatim from configuration and is passed straight to the wire; parsing it to Uri would lose credentials and query quirks cameras rely on")]
    public RtspReceiverContext(PipeReader reader, PipeWriter writer, string url, string streamName,
        EndPoint endPoint, string? userName, string? password, RtspDialect dialect, CancellationToken ct)
        : base(reader, writer, ct)
    {
        _url = url;
        StreamName = streamName;
        _endPoint = endPoint;
        _authenticator = new RtspAuthenticator(userName, password);
        _dialect = dialect;
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
        var adapter = new RtspMediaFrameAdapter(this);
        var flow = new RtspControlFlow(connection, _url, _authenticator, _dialect, adapter);

        connection.OnInterleaved = (channel, payload) => OnInterleavedAsync(flow, adapter, channel, payload);

        // the read loop must run while the handshake is in flight: it dispatches the
        // responses the handshake awaits (and answers server keepalive probes)
        Task<long> readLoop = connection.RunReadLoopAsync(CancellationToken).AsTask();

        try
        {
            await flow.RunAsync(CancellationToken).ConfigureAwait(false);
            Logger.ZLogInformation($"RTSP session established: {Id} ({_url})");

            using var keepAlive = StartKeepAlive(flow);
            await readLoop.ConfigureAwait(false); // media flows through OnInterleaved until the peer or we stop
        }
        finally
        {
            _completed = true;
            await flow.TeardownAsync(CancellationToken.None).ConfigureAwait(false);

            // drain: emit the frames already assembled before the pipeline finalizes
            if (MediaOutlet is not null && adapter.HasPendingFrames)
            {
                adapter.HasPendingFrames = false;
                await MediaOutlet.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Logger.ZLogInformation($"RTSP session ended: {Id}");
        }
    }

    private async ValueTask OnInterleavedAsync(RtspControlFlow flow, RtspMediaFrameAdapter adapter,
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
            var datagram = new ReadOnlySpan<byte>(rented, 0, (int)payload.Length);

            if (channel == track.RtcpChannel)
            {
                if (RtcpSenderReport.TryFindSenderReport(datagram, out RtcpSenderReport report))
                {
                    if (track.Kind == SdpMediaKind.Video)
                    {
                        adapter.OnVideoSenderReport(report);
                    }
                    else
                    {
                        adapter.OnAudioSenderReport(report);
                    }
                }
            }
            else if (RtpPacket.TryParse(datagram, out RtpPacket rtp))
            {
                if (track.Kind == SdpMediaKind.Video)
                {
                    adapter.FeedVideo(rtp);
                }
                else
                {
                    adapter.FeedAudio(rtp);
                }
            }
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
