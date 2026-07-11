using System.Buffers.Binary;

namespace Spangle.Codecs.Opus;

/// <summary>
/// Opus packet and OpusHead helpers (RFC 6716 / RFC 7845). The canonical MediaFrame
/// form carries the OpusHead identification header as the audio Config payload and
/// raw Opus packets as frames — the same shapes enhanced-RTMP and ISO-BMFF use.
/// </summary>
internal static class OpusPacket
{
    public const uint SampleRate = 48000; // the Opus output clock is always 48 kHz

    /// <summary>
    /// Duration of one Opus packet in 48 kHz samples, from the TOC byte
    /// (RFC 6716 3.1): config selects the frame size, the count code the frame count.
    /// Returns 0 for packets too broken to carry audio.
    /// </summary>
    public static uint GetSampleCount(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1)
        {
            return 0;
        }
        int config = packet[0] >> 3;
        uint frameSamples = config switch
        {
            < 12 => (config & 0x03) switch // SILK: 10/20/40/60 ms
            {
                0 => 480,
                1 => 960,
                2 => 1920,
                _ => 2880,
            },
            < 16 => (config & 0x01) == 0 ? 480u : 960u, // Hybrid: 10/20 ms
            _ => (config & 0x03) switch // CELT: 2.5/5/10/20 ms
            {
                0 => 120,
                1 => 240,
                2 => 480,
                _ => 960,
            },
        };
        uint frameCount = (packet[0] & 0x03) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length >= 2 ? (uint)(packet[1] & 0x3F) : 0,
        };
        return frameSamples * frameCount;
    }

    // =======================================================================
    // OpusHead (RFC 7845 5.1) — note: multi-byte fields are little-endian

    public const int OpusHeadMinSize = 19;
    private static readonly byte[] s_opusHeadMagic = "OpusHead"u8.ToArray();

    public static bool IsOpusHead(ReadOnlySpan<byte> config) =>
        config.Length >= OpusHeadMinSize && config[..8].SequenceEqual(s_opusHeadMagic);

    /// <summary>
    /// Synthesizes an OpusHead for sources that signal only a channel count
    /// (Opus over MPEG-TS). 3840 samples of pre-skip is the libopus standard delay.
    /// Channels beyond stereo use mapping family 1 with the RFC 7845 5.1.1.2
    /// (Vorbis-order) default layouts.
    /// </summary>
    public static byte[] BuildOpusHead(byte channels)
    {
        bool multi = channels > 2;
        var head = new byte[multi ? OpusHeadMinSize + 2 + channels : OpusHeadMinSize];
        s_opusHeadMagic.CopyTo(head, 0);
        head[8] = 1; // version
        head[9] = channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 3840); // pre-skip
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), SampleRate);
        // output gain 0
        if (multi)
        {
            head[18] = 1; // channel mapping family 1
            (byte streams, byte coupled, byte[] mapping) = channels switch
            {
                3 => ((byte)2, (byte)1, (byte[])[0, 2, 1]),
                4 => (2, 2, [0, 1, 2, 3]),
                5 => (3, 2, [0, 4, 1, 2, 3]),
                6 => (4, 2, [0, 4, 1, 2, 3, 5]),
                7 => (4, 3, [0, 4, 1, 2, 3, 5, 6]),
                _ => (5, 3, [0, 6, 1, 2, 3, 4, 5, 7]),
            };
            head[19] = streams;
            head[20] = coupled;
            mapping.CopyTo(head, 21);
        }
        return head;
    }

    public readonly record struct OpusHeadInfo(
        byte ChannelCount,
        ushort PreSkip,
        uint InputSampleRate,
        short OutputGain,
        byte ChannelMappingFamily);

    /// <summary>Reads the fixed part of an OpusHead; the mapping table (family != 0) follows at [19..].</summary>
    public static OpusHeadInfo ParseOpusHead(ReadOnlySpan<byte> config)
    {
        if (!IsOpusHead(config))
        {
            throw new InvalidDataException("Not an OpusHead identification header");
        }
        return new OpusHeadInfo(
            config[9],
            BinaryPrimitives.ReadUInt16LittleEndian(config[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(config[12..]),
            BinaryPrimitives.ReadInt16LittleEndian(config[16..]),
            config[18]);
    }
}
