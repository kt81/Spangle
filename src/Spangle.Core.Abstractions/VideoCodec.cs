using System.Diagnostics.CodeAnalysis;

namespace Spangle;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum VideoCodec : uint
{
    // Internal FourCC
    H264 = 'h' << 24 | '2' << 16 | '6' << 8 | '4',
    H265 = 'h' << 24 | '2' << 16 | '6' << 8 | '5',
    H266 = 'h' << 24 | '2' << 16 | '6' << 8 | '6',

    VP8 = 'v' << 24 | 'p' << 16 | '0' << 8 | '8',
    VP9 = 'v' << 24 | 'p' << 16 | '0' << 8 | '9',
    AV1 = 'a' << 24 | 'v' << 16 | '0' << 8 | '1',
}
