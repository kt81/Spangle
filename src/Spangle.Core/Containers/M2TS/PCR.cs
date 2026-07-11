using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

/// <summary>
/// Program Clock Reference: 33-bit base (90 kHz) + 6 reserved bits + 9-bit extension (27 MHz remainder).
/// </summary>
[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
internal partial struct PCR
{
    public const int Size = 6;

    [
        BitField(typeof(ulong), "Base", 33, description: "PCR base in 90 kHz units"),
        BitField(typeof(byte), "Reserved", 6, description: "Reserved bits; all 1 on the wire"),
        BitField(typeof(ushort), "Extension", 9, description: "PCR extension in 27 MHz units"),
    ]
    private InlineArray6<byte> _value;

    /// <summary>The full 27 MHz clock value</summary>
    public ulong Value => Base * 300 + Extension;
}
