using System.Runtime.InteropServices;

namespace Spangle.Containers.M2TS;

[StructLayout(LayoutKind.Sequential, Pack = Size, Size = Size)]
internal unsafe struct TSHeaderAdaptationFields
{
    public const int Size = 2;
    public const int LengthOffset = 0;
    public const int FlagsOffset = 1;

    private fixed byte _value[Size];

    private const int AdaptationFieldsOffset = 4;

    public readonly int AdaptationFieldLength => _value[LengthOffset];
    public readonly byte DiscontinuityIndicator => (byte)(_value[FlagsOffset] >>> 7);
    public readonly byte RandomAccessIndicator => (byte)((_value[FlagsOffset] & 0x40) >>> 6);
    public readonly byte ESPriorityIndicator => (byte)((_value[FlagsOffset] & 0x20) >>> 5);
    public readonly bool HasPCR => (_value[FlagsOffset] & 0x10) >>> 4 == 1;
    public readonly bool HasOPCR => (_value[FlagsOffset] & 0x08) >>> 3 == 1;
    public readonly bool HasSplicingPoint => (_value[FlagsOffset] & 0x04) >>> 2 == 1;
    public readonly bool HasTransportPrivateData => (_value[FlagsOffset] & 0x02) >>> 1 == 1;
    public readonly bool HasAdaptationFieldExtension => (_value[FlagsOffset] & 0x01) == 1;
}
