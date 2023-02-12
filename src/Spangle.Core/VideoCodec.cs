using System.Diagnostics.CodeAnalysis;

namespace Spangle;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum VideoCodec
{
    H264 = 1,
    H265 = 2,
    H266 = 3,

    VP8 = 11,
    VP9 = 12,
    AV1 = 13,
}
