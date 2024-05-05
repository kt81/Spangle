using System.Diagnostics.CodeAnalysis;

namespace Spangle;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum AudioCodec : uint
{
    // Internal FourCC
    MP3 = 'm' << 24 | 'p' << 16 | '0' << 8 | '3',
    AAC = 'm' << 24 | 'p' << 16 | '4' << 8 | 'a', // MPEG-4 AAC
}
