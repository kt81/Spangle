namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// One reassembled access unit handed from a video depacketizer to the adapter:
/// the NAL units of one picture plus its RTP timestamp.
/// </summary>
internal sealed class NalAccessUnit
{
    public List<byte[]> Nals { get; } = new(8);
    public uint RtpTimestamp { get; set; }

    public void Reset(uint rtpTimestamp)
    {
        Nals.Clear();
        RtpTimestamp = rtpTimestamp;
    }
}
