namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// Routes one RTP or RTCP datagram to the media adapter, independent of how it arrived
/// (TCP-interleaved or a UDP socket). Shared by the pull and push receivers so the two
/// transports feed the depacketizers through exactly one path.
/// </summary>
internal static class RtpDatagramDispatch
{
    public static void Dispatch<TContext>(RtspMediaFrameAdapter<TContext> adapter,
        Sdp.SdpMediaKind kind, bool isRtcp, ReadOnlySpan<byte> datagram)
        where TContext : ReceiverContextBase<TContext>
    {
        if (isRtcp)
        {
            if (RtcpSenderReport.TryFindSenderReport(datagram, out RtcpSenderReport report))
            {
                if (kind == Sdp.SdpMediaKind.Video)
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
            if (kind == Sdp.SdpMediaKind.Video)
            {
                adapter.FeedVideo(rtp);
            }
            else
            {
                adapter.FeedAudio(rtp);
            }
        }
    }
}
