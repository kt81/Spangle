using System.Text;

namespace Spangle.Codecs.Id3;

/// <summary>
/// Minimal ID3v2.4 tag writer for HLS timed metadata: one tag per event,
/// carrying a TXXX (user-defined text) frame. Players surface these as
/// (description, value) pairs (hls.js FRAG_PARSING_METADATA, Safari DataCue).
/// </summary>
internal static class Id3Tag
{
    private const int HeaderSize = 10;

    /// <summary>Builds a complete ID3v2.4 tag containing a single TXXX frame.</summary>
    public static byte[] BuildTxxx(string description, string value)
    {
        int descLen = Encoding.UTF8.GetByteCount(description);
        int valueLen = Encoding.UTF8.GetByteCount(value);
        int framePayload = 1 + descLen + 1 + valueLen; // encoding byte + desc NUL value
        int frameSize = HeaderSize + framePayload;     // frame header + payload
        var tag = new byte[HeaderSize + frameSize];

        // tag header
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 4; // v2.4
        WriteSyncSafe(tag.AsSpan(6), frameSize);

        // TXXX frame header
        var p = HeaderSize;
        tag[p] = (byte)'T';
        tag[p + 1] = (byte)'X';
        tag[p + 2] = (byte)'X';
        tag[p + 3] = (byte)'X';
        WriteSyncSafe(tag.AsSpan(p + 4), framePayload);
        p += HeaderSize;

        tag[p++] = 0x03; // UTF-8
        p += Encoding.UTF8.GetBytes(description, tag.AsSpan(p));
        tag[p++] = 0x00; // NUL terminator of the description
        Encoding.UTF8.GetBytes(value, tag.AsSpan(p));
        return tag;
    }

    /// <summary>28 significant bits, 7 per byte (bit 7 always 0)</summary>
    private static void WriteSyncSafe(Span<byte> dest, int value)
    {
        dest[0] = (byte)((value >> 21) & 0x7F);
        dest[1] = (byte)((value >> 14) & 0x7F);
        dest[2] = (byte)((value >> 7) & 0x7F);
        dest[3] = (byte)(value & 0x7F);
    }
}
