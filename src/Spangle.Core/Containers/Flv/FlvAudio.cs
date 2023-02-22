using System.Diagnostics.CodeAnalysis;
using Spangle.Rtmp;

namespace Spangle.Containers.Flv;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal enum FlvAudioCodec : byte
{
    PCM            = 0,
    ADPCM          = 1,
    MP3            = 2,
    PCM_LE         = 3,
    Asao16kHzMono  = 4,
    Asao8kHzMono   = 5,
    Asao           = 6,
    G711ALaw       = 7,
    G711MULaw      = 8,
    AAC            = 10,
    Speex          = 11,
    MP3_8kHz       = 14,
    DeviceSpecific = 15,

    // It is not official, but it is well known out there.
    CommunityOpus1 = 9,
    CommunityOpus2 = 13,
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal enum FlvAudioSampleRate :byte
{
    Rate5_5kHz = 0,
    Rate11kHz  = 1,
    Rate22kHz  = 2,
    Rate44kHz  = 3,
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal enum FlvAudioSampleSize : byte
{
    Size8Bit = 0,
    Size16Bit = 1,
}

/*
 SoundFormat UB[4]
 SoundRate   UB[2]
 SoundSize   UB[1]
 SoundType   UB[1]
 */
internal readonly struct FlvAudioControl
{
    private readonly byte _value;

    public FlvAudioControl(byte value)
    {
        _value = value;
    }

    public FlvAudioCodec Codec => (FlvAudioCodec)(_value >>> 4);
    public FlvAudioSampleRate SampleRate => (FlvAudioSampleRate)((_value >>> 2) & 0b0011);
    public FlvAudioSampleSize SampleSize => (FlvAudioSampleSize)(_value & 0b0001);
}

internal struct FlvAudio
{
    public readonly FlvAudioControl Control;
    public byte[] Data;
}

internal static class FlvAudioEnumExtensions
{
    public static AudioCodec ToInternal(this FlvAudioCodec codec)
    {
        return codec switch
        {
            FlvAudioCodec.MP3 => AudioCodec.MP3,
            FlvAudioCodec.AAC => AudioCodec.AAC,

            _ => throw new NotInScopeException($"Unsupported audio codec: {codec.ToString()})"),
        };
    }
}
