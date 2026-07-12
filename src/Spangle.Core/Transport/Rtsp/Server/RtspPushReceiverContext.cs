using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Spangle.Transport.Rtsp.Rtp;
using Spangle.Transport.Rtsp.Sdp;
using ZLogger;

namespace Spangle.Transport.Rtsp.Server;

/// <summary>
/// Accepts an RTSP push (this server is the RTSP server; a client RECORDs to it) and
/// emits canonical <see cref="Spangle.Spinner.MediaFrameHeader"/> frames, exactly like
/// the other receivers. TCP-interleaved transport. The publish target is the URL path
/// of the ANNOUNCE, so unlike the pull receiver this fits the RTMP/SRT publish model:
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

    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        using var connection = new RtspServerConnection(RemoteReader, RemoteWriter);
        var adapter = new RtspMediaFrameAdapter<RtspPushReceiverContext>(this);
        var flow = new RtspServerControlFlow(adapter);
        var gateOpened = false;

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
            if (MediaOutlet is not null && adapter.HasPendingFrames)
            {
                adapter.HasPendingFrames = false;
                await MediaOutlet.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Logger.ZLogInformation($"RTSP push connection closed: {_id}");
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
}
