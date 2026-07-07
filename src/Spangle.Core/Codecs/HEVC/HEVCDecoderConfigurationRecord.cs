using System.Buffers;

namespace Spangle.Codecs.HEVC;

/// <summary>
/// Minimal reader for HEVCDecoderConfigurationRecord (hvcC, ISO/IEC 14496-15 8.3.3.1).
/// Extracts the NALU length size and the parameter set NALUs (VPS/SPS/PPS).
/// </summary>
internal static class HEVCDecoderConfigurationRecord
{
    private const int FixedPartSize = 22;

    private static readonly byte[] s_startCode4 = [0x00, 0x00, 0x00, 0x01];

    /// <summary>
    /// Parses the record, writing all parameter set NALUs in Annex B form (4-byte start codes)
    /// into <paramref name="parameterSetsOut"/>.
    /// </summary>
    /// <returns>The NALU length size (1, 2 or 4) used by the stream</returns>
    public static int Parse(ReadOnlySequence<byte> buff, IBufferWriter<byte> parameterSetsOut)
    {
        // The fixed part: configurationVersion, profile/tier/level, ... , lengthSizeMinusOne (byte 21)
        Span<byte> fixedPart = stackalloc byte[FixedPartSize];
        buff.Slice(0, FixedPartSize).CopyTo(fixedPart);
        int lengthSize = (fixedPart[21] & 0b11) + 1;
        buff = buff.Slice(FixedPartSize);

        Span<byte> one = stackalloc byte[1];
        buff.Slice(0, 1).CopyTo(one);
        int numOfArrays = one[0];
        buff = buff.Slice(1);

        Span<byte> arrayHeader = stackalloc byte[3];
        Span<byte> naluLen = stackalloc byte[2];
        for (var i = 0; i < numOfArrays; i++)
        {
            // [array_completeness(1) reserved(1) NAL_unit_type(6)][numNalus(16)]
            buff.Slice(0, 3).CopyTo(arrayHeader);
            int numNalus = (arrayHeader[1] << 8) | arrayHeader[2];
            buff = buff.Slice(3);

            for (var j = 0; j < numNalus; j++)
            {
                buff.Slice(0, 2).CopyTo(naluLen);
                int len = (naluLen[0] << 8) | naluLen[1];
                buff = buff.Slice(2);

                parameterSetsOut.Write(s_startCode4);
                var nalu = buff.Slice(0, len);
                nalu.CopyTo(parameterSetsOut.GetSpan(len));
                parameterSetsOut.Advance(len);
                buff = buff.Slice(len);
            }
        }

        return lengthSize;
    }
}
