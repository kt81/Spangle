namespace Spangle.Codecs.AVC;

/// <summary>
/// H.264 SPS parser (ITU-T H.264 7.3.2.1.1) — just deep enough to reach the frame
/// dimensions, which the fMP4 sample entry needs (MSE rejects a 0x0 coded size).
/// </summary>
internal static class AvcSps
{
    private static readonly int[] s_highProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    /// <summary>
    /// Display dimensions from an SPS NALU (including its 1-byte NAL header).
    /// Throws <see cref="InvalidDataException"/> on broken input.
    /// </summary>
    public static (uint Width, uint Height) ParseDimensions(ReadOnlySpan<byte> sps)
    {
        Span<byte> rbsp = sps.Length <= 256 ? stackalloc byte[sps.Length] : new byte[sps.Length];
        int rbspLength = RbspBitReader.Unescape(sps[1..], rbsp);
        var r = new RbspBitReader(rbsp[..rbspLength]);

        var profileIdc = (int)r.ReadBits(8);
        r.ReadBits(8); // constraint flags + reserved
        r.ReadBits(8); // level_idc
        r.ReadUe();    // seq_parameter_set_id

        ulong chromaFormatIdc = 1; // 4:2:0 is implied outside the high-profile branch
        if (s_highProfiles.Contains(profileIdc))
        {
            chromaFormatIdc = r.ReadUe();
            if (chromaFormatIdc == 3)
            {
                r.ReadBit(); // separate_colour_plane_flag
            }
            r.ReadUe(); // bit_depth_luma_minus8
            r.ReadUe(); // bit_depth_chroma_minus8
            r.ReadBit(); // qpprime_y_zero_transform_bypass_flag
            if (r.ReadBit()) // seq_scaling_matrix_present_flag
            {
                int lists = chromaFormatIdc == 3 ? 12 : 8;
                for (var i = 0; i < lists; i++)
                {
                    if (r.ReadBit()) // seq_scaling_list_present_flag[i]
                    {
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        r.ReadUe(); // log2_max_frame_num_minus4
        ulong picOrderCntType = r.ReadUe();
        if (picOrderCntType == 0)
        {
            r.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (picOrderCntType == 1)
        {
            r.ReadBit(); // delta_pic_order_always_zero_flag
            r.ReadSe();  // offset_for_non_ref_pic
            r.ReadSe();  // offset_for_top_to_bottom_field
            ulong cycles = r.ReadUe();
            for (ulong i = 0; i < cycles; i++)
            {
                r.ReadSe();
            }
        }

        r.ReadUe();  // max_num_ref_frames
        r.ReadBit(); // gaps_in_frame_num_value_allowed_flag

        ulong widthInMbs = r.ReadUe() + 1;
        ulong heightInMapUnits = r.ReadUe() + 1;
        bool frameMbsOnly = r.ReadBit();
        if (!frameMbsOnly)
        {
            r.ReadBit(); // mb_adaptive_frame_field_flag
        }
        r.ReadBit(); // direct_8x8_inference_flag

        ulong width = widthInMbs * 16;
        ulong height = heightInMapUnits * 16 * (frameMbsOnly ? 1UL : 2UL);
        if (r.ReadBit()) // frame_cropping_flag
        {
            ulong cropLeft = r.ReadUe();
            ulong cropRight = r.ReadUe();
            ulong cropTop = r.ReadUe();
            ulong cropBottom = r.ReadUe();
            // crop units per 7.4.2.1.1: horizontal SubWidthC, vertical SubHeightC x frame factor
            ulong cropUnitX = chromaFormatIdc is 1 or 2 ? 2UL : 1UL;
            ulong cropUnitY = (chromaFormatIdc == 1 ? 2UL : 1UL) * (frameMbsOnly ? 1UL : 2UL);
            width -= (cropLeft + cropRight) * cropUnitX;
            height -= (cropTop + cropBottom) * cropUnitY;
        }

        return ((uint)width, (uint)height);
    }

    /// <summary>Display dimensions from an avcC record (first SPS).</summary>
    public static (uint Width, uint Height) ParseDimensionsFromRecord(ReadOnlySpan<byte> avcc)
    {
        if (avcc.Length < 9 || (avcc[5] & 0x1F) == 0)
        {
            throw new InvalidDataException("The avcC record contains no SPS");
        }
        int length = (avcc[6] << 8) | avcc[7];
        return ParseDimensions(avcc.Slice(8, length));
    }

    private static void SkipScalingList(ref RbspBitReader r, int size)
    {
        long lastScale = 8;
        long nextScale = 8;
        for (var i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                nextScale = (lastScale + r.ReadSe() + 256) % 256;
            }
            if (nextScale != 0)
            {
                lastScale = nextScale;
            }
        }
    }
}
