using System.Runtime.InteropServices;

namespace Spangle.Containers.M2TS;

[StructLayout(LayoutKind.Auto, Pack = 2, Size = Size)]
internal unsafe struct PCR
{
    public const int Size = 6;

    private fixed byte _value[Size];

    public ulong BasePCR => (ulong)(
        (_value[0] << 25)
        + (_value[1] << 17)
        + (_value[2] << 9)
        + (_value[3] << 1)
        + (_value[4] >>> 7)
    );

    // 6bit reserved: _value[4] & 0b0111_1110

    public ulong ExtensionPCR => (ulong)(
        ((_value[4] & 0x01) << 8)
        + _value[5]
    );

    public ulong Value => BasePCR * 300 + ExtensionPCR;
}
