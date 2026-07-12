using System.Buffers.Binary;

namespace Spangle.Codecs.AVC;

/// <summary>
/// Builds an ISO 14496-15 AVCDecoderConfigurationRecord (avcC) from raw SPS/PPS
/// NAL units — the canonical video Config payload of the MediaFrame boundary.
/// Shared by every ingest that receives bare parameter sets (TS elementary
/// streams, RTP) rather than a ready-made record (FLV).
/// </summary>
internal static class AvcCBuilder
{
    /// <summary>One SPS and one PPS, 4-byte NALU length prefixes declared.</summary>
    public static byte[] Build(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        var avcc = new byte[11 + sps.Length + pps.Length];
        avcc[0] = 1;       // configurationVersion
        avcc[1] = sps[1];  // AVCProfileIndication
        avcc[2] = sps[2];  // profile_compatibility
        avcc[3] = sps[3];  // AVCLevelIndication
        avcc[4] = 0xFF;    // reserved(6) + lengthSizeMinusOne = 3 (4-byte lengths)
        avcc[5] = 0xE1;    // reserved(3) + numOfSequenceParameterSets = 1
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(6), (ushort)sps.Length);
        sps.CopyTo(avcc.AsSpan(8));
        int p = 8 + sps.Length;
        avcc[p] = 1;       // numOfPictureParameterSets
        BinaryPrimitives.WriteUInt16BigEndian(avcc.AsSpan(p + 1), (ushort)pps.Length);
        pps.CopyTo(avcc.AsSpan(p + 3));
        return avcc;
    }
}
