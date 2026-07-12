namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>A codec-specific RTP video depacketizer; feeds packets, emits access units via its callback.</summary>
internal interface IVideoDepacketizer
{
    void Feed(in RtpPacket packet);
}
