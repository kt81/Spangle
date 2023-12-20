using System.Diagnostics.CodeAnalysis;
using Spangle.Protocols.Rtmp;

namespace Spangle.Containers.Flv;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal enum FlvVideoCodec : uint
{
    H263     = 2,
    ScreenV1 = 3,
    VP6      = 4,
    VP6Alpha = 5,
    ScreenV2 = 6,
    H264     = 7,

    // These are not in the FLV document, but are fully accepted by the community.
    CommunityRealH263 = 8,
    CommunityMPEG4    = 9,

    // These are not official codec IDs, but they are fairly well known.
    CommunityH265 = 12,
    CommunityAV1  = 13,
    CommunityVP8  = 14,
    CommunityVP9  = 15,

    // enhanced-rtmp FourCC
    // https://www.w3.org/TR/webcodecs-codec-registry/#video-codec-registry
    EnhancedAV1     = 'a' << 24 | 'v' << 16 | '0' << 8 | '1',
    EnhancedAVC1    = 'a' << 24 | 'v' << 16 | 'c' << 8 | '1',
    EnhancedAVC3    = 'a' << 24 | 'v' << 16 | 'c' << 8 | '3',
    EnhancedHEVC    = 'h' << 24 | 'v' << 16 | 'c' << 8 | '1',
    EnhancedHEVCAlt = 'h' << 24 | 'e' << 16 | 'v' << 8 | '1',
    EnhancedVP9     = 'v' << 24 | 'p' << 16 | '0' << 8 | '9',
}

internal enum FlvVideoFrameType : uint
{
    Keyframe             = 1,
    InterFrame           = 2,
    DisposableInterFrame = 3,
    GeneratedKeyframe    = 4,
    MiscFrame            = 5, // video info / command frame
}

/*
 FrameType UB[4]
 CodecID   UB[4]
 */
internal readonly struct FlvVideoControl
{
    private readonly byte _value;

    public FlvVideoControl(byte value)
    {
        _value = value;
    }

    public FlvVideoFrameType FrameType => (FlvVideoFrameType)(_value >>> 4);
    public FlvVideoCodec Codec => (FlvVideoCodec)(_value & 0b1111);
}

internal static class FlvVideoCodecExtensions
{
    public static VideoCodec ToInternal(this FlvVideoCodec codec)
    {
        return codec switch
        {
            FlvVideoCodec.H264 => VideoCodec.H264,

            _ => throw new NotInScopeException($"Unsupported video codec: {codec.ToString()})"),
        };
    }
}
