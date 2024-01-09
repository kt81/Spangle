using System.Diagnostics.CodeAnalysis;
using Spangle.Transport.Rtmp;

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
    // AVCs are not in enhanced-rtmp spec, but are defined in the link above.
    EnhancedAVC1    = 'a' << 24 | 'v' << 16 | 'c' << 8 | '1',
    EnhancedAVC3    = 'a' << 24 | 'v' << 16 | 'c' << 8 | '3',
    // 'hvc1' is the official FourCC for HEVC in enhanced-rtmp spec, not 'hev1'.
    EnhancedHEVC    = 'h' << 24 | 'v' << 16 | 'c' << 8 | '1',
    EnhancedHEVCAlt = 'h' << 24 | 'e' << 16 | 'v' << 8 | '1',
    EnhancedVP9     = 'v' << 24 | 'p' << 16 | '0' << 8 | '9',
}

/// <summary>
/// Packet type.
/// This is available only in enhanced-rtmp.
/// </summary>
internal enum FlvVideoPacketType : byte
{
    PacketTypeSequenceStart = 0,
    PacketTypeCodedFrames,
    PackoetTypeSequenceEnd,

    // CompositionTime Offset is implied to equal zero. This is
    // an optimization to save putting SI24 composition time value of zero on
    // the wire. See pseudo code below in the VideoTagBody section
    PacketTypeCodedFramesX,

    // VideoTagBody does not contain video data. VideoTagBody
    // instead contains an AMF encoded metadata. See Metadata Frame
    // section for an illustration of its usage. As an example, the metadata
    // can be HDR information. This is a good way to signal HDR
    // information. This also opens up future ways to express additional
    // metadata that is meant for the next video sequence.
    //
    // note: presence of PacketTypeMetadata means that FrameTy
    PacketTypeMetadata,

    // Carriage of bitstream in MPEG-2 TS format
    // note: PacketTypeSequenceStart and PacketTypeMPEG2TSSequenceStart
    // are mutually exclusive
    PacketTypeMPEG2TSSequenceStart,

    // 6-15 = reserved
}

[Flags]
internal enum FlvVideoFrameType : byte
{
    Keyframe             = 1,
    InterFrame           = 2,
    DisposableInterFrame = 3,
    GeneratedKeyframe    = 4,
    MiscFrame            = 5, // video info / command frame

    Enhanced = 0b1000, // enhanced-rtmp
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

    public bool IsEnhanced => _value >>> 7 == 1;
    public FlvVideoFrameType FrameType => (FlvVideoFrameType)((_value >>> 4) & 0b0111);
    public FlvVideoCodec Codec => (FlvVideoCodec)(_value & 0b1111);
    public FlvVideoPacketType VideoPacketType => (FlvVideoPacketType)(_value & 0b1111);
}

internal static class FlvVideoCodecExtensions
{
    public static VideoCodec ToInternal(this FlvVideoCodec codec)
    {
        return codec switch
        {
            FlvVideoCodec.H264
                or FlvVideoCodec.EnhancedAVC1
                or FlvVideoCodec.EnhancedAVC3
                => VideoCodec.H264,

            FlvVideoCodec.CommunityH265
                or FlvVideoCodec.EnhancedHEVC
                or FlvVideoCodec.EnhancedHEVCAlt
                => VideoCodec.H265,

            FlvVideoCodec.CommunityVP9
                or FlvVideoCodec.EnhancedVP9
                => VideoCodec.VP9,

            FlvVideoCodec.CommunityAV1
                or FlvVideoCodec.EnhancedAV1
                => VideoCodec.AV1,

            // We can't find the meaning of supporting other codecs
            _ => throw new NotInScopeException($"Unsupported video codec: {codec.ToString()})"),
        };
    }

    public static VideoCodec ParseToInternal(uint flvVideoCodecId)
    {
        return ((FlvVideoCodec)flvVideoCodecId).ToInternal();
    }
}
