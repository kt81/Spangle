using Spangle.Codecs.AVC;

namespace Spangle.Tests.Codecs;

/// <summary>
/// The H.264 SPS dimension parser feeds the fMP4 sample entry; MSE players reject
/// a 0x0 coded size, so this must hold for the common encoder outputs.
/// The SPS is composed bit-by-bit, so the expectations are exact.
/// </summary>
public class AvcSpsTests
{
    [Fact]
    public void BaselineSpsWithCroppingYieldsDisplayDimensions()
    {
        // 640x360: 40x23 macroblocks (640x368 coded) with a bottom crop of 4 chroma units
        byte[] sps = BuildBaselineSps(widthMbs: 40, heightMapUnits: 23, cropBottom: 4);

        (uint width, uint height) = AvcSps.ParseDimensions(sps);

        width.Should().Be(640u);
        height.Should().Be(360u);
    }

    [Fact]
    public void UncroppedSpsYieldsCodedDimensions()
    {
        byte[] sps = BuildBaselineSps(widthMbs: 80, heightMapUnits: 45, cropBottom: 0); // 1280x720

        (uint width, uint height) = AvcSps.ParseDimensions(sps);

        width.Should().Be(1280u);
        height.Should().Be(720u);
    }

    [Fact]
    public void BrokenSpsThrowsInvalidData()
    {
        var act = () => AvcSps.ParseDimensions([0x67, 0x42, 0xC0]);
        act.Should().Throw<InvalidDataException>();
    }

    // =======================================================================

    private static byte[] BuildBaselineSps(uint widthMbs, uint heightMapUnits, uint cropBottom)
    {
        var w = new BitWriter();
        w.WriteBits(66, 8);  // profile_idc: Baseline
        w.WriteBits(0xC0, 8); // constraint flags
        w.WriteBits(30, 8);  // level_idc
        w.WriteUe(0);        // seq_parameter_set_id
        w.WriteUe(0);        // log2_max_frame_num_minus4
        w.WriteUe(0);        // pic_order_cnt_type
        w.WriteUe(0);        // log2_max_pic_order_cnt_lsb_minus4
        w.WriteUe(0);        // max_num_ref_frames
        w.WriteBits(0, 1);   // gaps_in_frame_num_value_allowed_flag
        w.WriteUe(widthMbs - 1);
        w.WriteUe(heightMapUnits - 1);
        w.WriteBits(1, 1);   // frame_mbs_only_flag
        w.WriteBits(1, 1);   // direct_8x8_inference_flag
        if (cropBottom > 0)
        {
            w.WriteBits(1, 1); // frame_cropping_flag
            w.WriteUe(0);
            w.WriteUe(0);
            w.WriteUe(0);
            w.WriteUe(cropBottom);
        }
        else
        {
            w.WriteBits(0, 1);
        }
        w.WriteBits(1, 1);   // rbsp_stop_one_bit

        byte[] rbsp = w.ToArray();
        var sps = new byte[1 + rbsp.Length];
        sps[0] = 0x67; // NAL header: SPS
        rbsp.CopyTo(sps, 1);
        return sps;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bitPos;

        public void WriteBits(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if ((_bitPos & 7) == 0)
                {
                    _bytes.Add(0);
                }
                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (7 - (_bitPos & 7)));
                }
                _bitPos++;
            }
        }

        public void WriteUe(ulong value)
        {
            int leadingZeros = 63 - System.Numerics.BitOperations.LeadingZeroCount(value + 1UL);
            WriteBits(0, leadingZeros);
            WriteBits(value + 1UL, leadingZeros + 1);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
