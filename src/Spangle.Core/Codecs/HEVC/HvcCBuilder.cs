using System.Buffers.Binary;

namespace Spangle.Codecs.HEVC;

/// <summary>
/// Builds an HEVCDecoderConfigurationRecord (hvcC, ISO/IEC 14496-15 8.3.3.1) from
/// in-band VPS/SPS/PPS NAL units — the write-side counterpart of
/// <see cref="HEVCDecoderConfigurationRecord"/>, used by TS ingest where parameter
/// sets arrive inside the elementary stream instead of a configuration record.
/// </summary>
internal static class HvcCBuilder
{
    /// <summary>Fields extracted from the SPS that the record (and stream metadata) need.</summary>
    public readonly record struct SpsSummary(
        byte ProfileSpaceTierIdc,
        uint ProfileCompatibilityFlags,
        ulong ConstraintIndicatorFlags, // 48 bits
        byte LevelIdc,
        byte ChromaFormatIdc,
        byte BitDepthLumaMinus8,
        byte BitDepthChromaMinus8,
        byte MaxSubLayersMinus1,
        bool TemporalIdNested,
        uint Width,
        uint Height);

    public static byte[] Build(ReadOnlySpan<byte> vps, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps,
        out SpsSummary summary)
    {
        summary = ParseSps(sps);

        int size = 23
                   + 3 + 2 + vps.Length
                   + 3 + 2 + sps.Length
                   + 3 + 2 + pps.Length;
        var hvcc = new byte[size];

        hvcc[0] = 1; // configurationVersion
        hvcc[1] = summary.ProfileSpaceTierIdc;
        BinaryPrimitives.WriteUInt32BigEndian(hvcc.AsSpan(2), summary.ProfileCompatibilityFlags);
        // 48-bit constraint flags
        for (var i = 0; i < 6; i++)
        {
            hvcc[6 + i] = (byte)(summary.ConstraintIndicatorFlags >> (8 * (5 - i)));
        }
        hvcc[12] = summary.LevelIdc;
        hvcc[13] = 0xF0; // reserved(4) + min_spatial_segmentation_idc(12) = 0
        hvcc[14] = 0x00;
        hvcc[15] = 0xFC; // reserved(6) + parallelismType(2) = 0
        hvcc[16] = (byte)(0xFC | summary.ChromaFormatIdc);
        hvcc[17] = (byte)(0xF8 | summary.BitDepthLumaMinus8);
        hvcc[18] = (byte)(0xF8 | summary.BitDepthChromaMinus8);
        hvcc[19] = 0; // avgFrameRate = 0 (unspecified)
        hvcc[20] = 0;
        // constantFrameRate(2)=0 | numTemporalLayers(3) | temporalIdNested(1) | lengthSizeMinusOne(2)=3
        hvcc[21] = (byte)(((summary.MaxSubLayersMinus1 + 1) << 3)
                          | (summary.TemporalIdNested ? 1 << 2 : 0)
                          | 0b11);
        hvcc[22] = 3; // numOfArrays

        int pos = 23;
        pos = WriteArray(hvcc, pos, 32, vps);
        pos = WriteArray(hvcc, pos, 33, sps);
        WriteArray(hvcc, pos, 34, pps);
        return hvcc;
    }

    private static int WriteArray(byte[] hvcc, int pos, byte nalType, ReadOnlySpan<byte> nalu)
    {
        hvcc[pos] = (byte)(0x80 | nalType); // array_completeness=1
        hvcc[pos + 1] = 0;                  // numNalus = 1
        hvcc[pos + 2] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(hvcc.AsSpan(pos + 3), (ushort)nalu.Length);
        nalu.CopyTo(hvcc.AsSpan(pos + 5));
        return pos + 5 + nalu.Length;
    }

    // =======================================================================
    // SPS parsing (ITU-T H.265 7.3.2.2.1), up to the bit depths

    private static SpsSummary ParseSps(ReadOnlySpan<byte> sps)
    {
        // strip the 2-byte NAL header, unescape the RBSP
        Span<byte> rbsp = sps.Length <= 256 ? stackalloc byte[sps.Length] : new byte[sps.Length];
        int rbspLength = UnescapeRbsp(sps[2..], rbsp);
        var r = new BitReader(rbsp[..rbspLength]);

        r.ReadBits(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = (byte)r.ReadBits(3);
        bool temporalIdNested = r.ReadBit();

        // profile_tier_level(1, maxSubLayersMinus1) — general part
        var profileSpaceTierIdc = (byte)r.ReadBits(8);
        var compat = (uint)r.ReadBits(32);
        ulong constraint = ((ulong)r.ReadBits(24) << 24) | r.ReadBits(24);
        var levelIdc = (byte)r.ReadBits(8);

        // sub-layer presence flags and their PTL blocks
        if (maxSubLayersMinus1 > 0)
        {
            Span<bool> profilePresent = stackalloc bool[8];
            Span<bool> levelPresent = stackalloc bool[8];
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = r.ReadBit();
                levelPresent[i] = r.ReadBit();
            }
            for (int i = maxSubLayersMinus1; i < 8; i++)
            {
                r.ReadBits(2); // reserved_zero_2bits
            }
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i])
                {
                    r.ReadBits(32); // profile space/tier/idc + compat (part 1)
                    r.ReadBits(24);
                    r.ReadBits(32); // constraint flags
                    r.ReadBits(24);
                }
                if (levelPresent[i])
                {
                    r.ReadBits(8);
                }
            }
        }

        r.ReadUe(); // sps_seq_parameter_set_id
        var chromaFormatIdc = (byte)r.ReadUe();
        if (chromaFormatIdc == 3)
        {
            r.ReadBit(); // separate_colour_plane_flag
        }
        uint width = (uint)r.ReadUe();  // pic_width_in_luma_samples
        uint height = (uint)r.ReadUe(); // pic_height_in_luma_samples
        if (r.ReadBit()) // conformance_window_flag
        {
            // The luma sample counts are the coded size (padded to whole CTUs); the
            // window crops them to the display size (e.g. 1920x1088 -> 1920x1080),
            // in units of SubWidthC/SubHeightC (7.4.3.2.1)
            var left = (uint)r.ReadUe();
            var right = (uint)r.ReadUe();
            var top = (uint)r.ReadUe();
            var bottom = (uint)r.ReadUe();
            uint subWidthC = chromaFormatIdc is 1 or 2 ? 2u : 1u;
            uint subHeightC = chromaFormatIdc == 1 ? 2u : 1u;
            width -= (left + right) * subWidthC;
            height -= (top + bottom) * subHeightC;
        }
        var bitDepthLumaMinus8 = (byte)r.ReadUe();
        var bitDepthChromaMinus8 = (byte)r.ReadUe();

        return new SpsSummary(profileSpaceTierIdc, compat, constraint, levelIdc,
            chromaFormatIdc, bitDepthLumaMinus8, bitDepthChromaMinus8,
            maxSubLayersMinus1, temporalIdNested, width, height);
    }

    /// <summary>Removes emulation prevention bytes (00 00 03 → 00 00).</summary>
    private static int UnescapeRbsp(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        var written = 0;
        var zeros = 0;
        for (var i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue; // skip the emulation prevention byte
            }
            zeros = b == 0 ? zeros + 1 : 0;
            dest[written++] = b;
        }
        return written;
    }

    /// <summary>MSB-first bit reader with Exp-Golomb support.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitPos;

        public bool ReadBit()
        {
            int byteIndex = _bitPos >> 3;
            int bitIndex = 7 - (_bitPos & 7);
            _bitPos++;
            return ((_data[byteIndex] >> bitIndex) & 1) != 0;
        }

        public ulong ReadBits(int count)
        {
            ulong value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | (ReadBit() ? 1UL : 0UL);
            }
            return value;
        }

        /// <summary>ue(v): unsigned Exp-Golomb</summary>
        public ulong ReadUe()
        {
            var leadingZeros = 0;
            while (!ReadBit())
            {
                leadingZeros++;
                if (leadingZeros > 32)
                {
                    throw new InvalidDataException("Broken Exp-Golomb code in SPS");
                }
            }
            return (1UL << leadingZeros) - 1 + ReadBits(leadingZeros);
        }
    }
}
