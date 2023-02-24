﻿using System.Diagnostics.CodeAnalysis;
using Spangle.Rtmp;

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

    public override string ToString()
    {
        return $$"""FlvVideoControl {frameType:{{FrameType}}, codec:{{Codec}}}""";
    }
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
