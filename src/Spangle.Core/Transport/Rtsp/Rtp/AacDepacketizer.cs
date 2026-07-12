using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// RFC 3640 (mpeg4-generic) AAC depacketization. The AU-header field widths come
/// from the SDP fmtp (sizeLength/indexLength/indexDeltaLength — AAC-hbr is
/// 13/3/3); each packet carries one or more complete access units. Fragmented
/// AUs (an AU larger than the MTU) do not occur over TCP-interleaved transport
/// and are dropped with a warning if a peer produces them anyway.
/// </summary>
internal sealed class AacDepacketizer(int sizeLength, int indexLength, int indexDeltaLength,
    Action<byte[], uint, int> onAccessUnit)
{
    private static readonly ILogger<AacDepacketizer> s_logger = SpangleLogManager.GetLogger<AacDepacketizer>();

    /// <summary>Feeds one RTP packet; AUs are reported as (au, rtpTimestamp, indexInPacket).</summary>
    public void Feed(in RtpPacket packet)
    {
        ReadOnlySpan<byte> payload = packet.Payload;
        if (sizeLength == 0)
        {
            // no AU headers at all (constantSize streams): the payload is one AU
            onAccessUnit(payload.ToArray(), packet.Timestamp, 0);
            return;
        }
        if (payload.Length < 2)
        {
            return;
        }

        int auHeadersLengthBits = (payload[0] << 8) | payload[1];
        int auHeadersLengthBytes = (auHeadersLengthBits + 7) / 8;
        if (payload.Length < 2 + auHeadersLengthBytes)
        {
            s_logger.ZLogWarning($"Truncated AAC AU-headers section; packet dropped");
            return;
        }

        ReadOnlySpan<byte> headers = payload.Slice(2, auHeadersLengthBytes);
        ReadOnlySpan<byte> data = payload[(2 + auHeadersLengthBytes)..];

        int bitsPerHeader = sizeLength + indexLength; // the first header uses indexLength
        var bitPos = 0;
        var auIndex = 0;
        var dataPos = 0;
        while (bitPos + sizeLength <= auHeadersLengthBits)
        {
            int auSize = (int)ReadBits(headers, bitPos, sizeLength);
            bitPos += bitsPerHeader;
            if (auIndex > 0)
            {
                bitPos += indexDeltaLength - indexLength; // subsequent headers use indexDeltaLength
            }

            if (dataPos + auSize > data.Length)
            {
                s_logger.ZLogWarning($"AAC AU exceeds the packet payload ({auSize} of {data.Length - dataPos} bytes); a fragmented AU is not supported");
                return;
            }
            onAccessUnit(data.Slice(dataPos, auSize).ToArray(), packet.Timestamp, auIndex);
            dataPos += auSize;
            auIndex++;
        }
    }

    private static ulong ReadBits(ReadOnlySpan<byte> buff, int bitOffset, int bitCount)
    {
        ulong value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            int bit = bitOffset + i;
            value = (value << 1) | (uint)((buff[bit >> 3] >> (7 - (bit & 7))) & 1);
        }
        return value;
    }
}
