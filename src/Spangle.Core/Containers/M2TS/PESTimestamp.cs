using System.Runtime.InteropServices;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

/// <summary>
/// PES PTS/DTS field (ISO/IEC 13818-1 2.4.3.7): a 33-bit timestamp split 3/15/15
/// by constant marker bits.
/// </summary>
[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
internal unsafe partial struct PESTimestamp
{
    public const int Size = 5;

    public const byte PrefixPtsOnly  = 0b0010;
    public const byte PrefixPtsOfPair = 0b0011;
    public const byte PrefixDts      = 0b0001;

    [
        BitField(typeof(byte), "Prefix", 4, description: "'0010' PTS-only / '0011' PTS of a PTS+DTS pair / '0001' DTS"),
        BitField(typeof(ulong), "Value", 3, description: "Timestamp bits 32..30 (90 kHz)"),
        BitFieldConst("Marker1", 1, 1),
        BitField(typeof(ulong), "Value", 15, description: "Timestamp bits 29..15"),
        BitFieldConst("Marker2", 1, 1),
        BitField(typeof(ulong), "Value", 15, description: "Timestamp bits 14..0"),
        BitFieldConst("Marker3", 1, 1),
    ]
    private fixed byte _value[Size];
}
