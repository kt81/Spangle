using System.Runtime.InteropServices;

namespace Spangle.Codecs.AAC;

/// <summary>
/// AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1), the 2+ byte AAC configuration
/// carried in FLV AAC sequence headers and in the mp4a esds box.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct AudioSpecificConfig
{
    public required byte AudioObjectType { get; init; }
    public required byte SamplingFrequencyIndex { get; init; }
    public required byte ChannelConfiguration { get; init; }

    private static readonly uint[] s_sampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350,
    ];

    public uint SampleRate => SamplingFrequencyIndex < s_sampleRates.Length
        ? s_sampleRates[SamplingFrequencyIndex]
        : throw new InvalidDataException($"Unsupported sampling frequency index: {SamplingFrequencyIndex}");

    public static AudioSpecificConfig Parse(ReadOnlySpan<byte> asc)
    {
        if (asc.Length < 2)
        {
            throw new InvalidDataException("AudioSpecificConfig must be at least 2 bytes");
        }
        return new AudioSpecificConfig
        {
            AudioObjectType = (byte)(asc[0] >> 3),
            SamplingFrequencyIndex = (byte)(((asc[0] & 0x07) << 1) | (asc[1] >> 7)),
            ChannelConfiguration = (byte)((asc[1] >> 3) & 0x0F),
        };
    }
}
