using System.Diagnostics.CodeAnalysis;
using Spangle.Rtmp;

namespace Spangle.Containers.Flv;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum FlvAudioCodec : uint
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

public static class FlvAudioCodecExtensions
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
