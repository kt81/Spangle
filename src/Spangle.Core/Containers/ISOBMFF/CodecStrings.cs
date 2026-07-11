using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Spangle.Containers.ISOBMFF;

/// <summary>
/// RFC 6381 codec strings for manifests (DASH MPD, HLS CODECS attribute),
/// derived from the codec configuration records the pipeline already carries.
/// </summary>
internal static class CodecStrings
{
    /// <summary>"avc1.PPCCLL" from the avcC profile/compat/level bytes</summary>
    public static string FromAvcC(ReadOnlySpan<byte> avcc) =>
        $"avc1.{avcc[1]:X2}{avcc[2]:X2}{avcc[3]:X2}";

    /// <summary>
    /// "hvc1.[space]idc.compat.[L|H]level(.constraints…)" per ISO 14496-15 E.3;
    /// the 32 compatibility bits go in bit-reversed, constraints trim trailing zeros.
    /// </summary>
    public static string FromHvcC(ReadOnlySpan<byte> hvcc)
    {
        byte ptl = hvcc[1];
        int profileSpace = ptl >> 6;
        bool highTier = (ptl & 0x20) != 0;
        int profileIdc = ptl & 0x1F;
        uint compat = ReverseBits(BinaryPrimitives.ReadUInt32BigEndian(hvcc[2..6]));
        byte levelIdc = hvcc[12];

        var sb = new StringBuilder("hvc1.");
        if (profileSpace > 0)
        {
            sb.Append((char)('A' + profileSpace - 1));
        }
        sb.Append(profileIdc);
        sb.Append('.').Append(compat.ToString("X", CultureInfo.InvariantCulture));
        sb.Append('.').Append(highTier ? 'H' : 'L').Append(levelIdc);

        int lastNonZero = -1;
        for (var i = 0; i < 6; i++)
        {
            if (hvcc[6 + i] != 0)
            {
                lastNonZero = i;
            }
        }
        for (var i = 0; i <= lastNonZero; i++)
        {
            sb.Append('.').Append(hvcc[6 + i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>"av01.P.LLT.BB" (profile, level+tier, bit depth) from the av1C record</summary>
    public static string FromAv1C(ReadOnlySpan<byte> av1c)
    {
        int profile = av1c[1] >> 5;
        int level = av1c[1] & 0x1F;
        bool highTier = (av1c[2] & 0x80) != 0;
        bool highBitDepth = (av1c[2] & 0x40) != 0;
        bool twelveBit = (av1c[2] & 0x20) != 0;
        int bitDepth = highBitDepth ? twelveBit ? 12 : 10 : 8;
        return $"av01.{profile}.{level:D2}{(highTier ? 'H' : 'M')}.{bitDepth:D2}";
    }

    /// <summary>Audio codec string: "mp4a.40.AOT" for AAC (from the ASC), "opus" for Opus</summary>
    public static string? FromAudio(AudioCodec codec, ReadOnlySpan<byte> config) => codec switch
    {
        AudioCodec.AAC when config.Length >= 1 => $"mp4a.40.{config[0] >> 3}",
        AudioCodec.Opus => "opus",
        _ => null,
    };

    private static uint ReverseBits(uint value)
    {
        value = (value >> 16) | (value << 16);
        value = ((value & 0xFF00FF00) >> 8) | ((value & 0x00FF00FF) << 8);
        value = ((value & 0xF0F0F0F0) >> 4) | ((value & 0x0F0F0F0F) << 4);
        value = ((value & 0xCCCCCCCC) >> 2) | ((value & 0x33333333) << 2);
        return ((value & 0xAAAAAAAA) >> 1) | ((value & 0x55555555) << 1);
    }
}
