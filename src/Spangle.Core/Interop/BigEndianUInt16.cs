using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 2 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public struct BigEndianUInt16 : IInteropType<ushort, BigEndianUInt16>
{
    private const int Length = sizeof(ushort);

    private InlineArray2<byte> _val;

    public static BigEndianUInt16 FromHost(ushort value)
    {
        var self = new BigEndianUInt16
        {
            HostValue = value
        };
        return self;
    }

    public ushort HostValue
    {
        readonly get => BinaryPrimitives.ReadUInt16BigEndian(_val);
        init => BinaryPrimitives.WriteUInt16BigEndian(_val, value);
    }

    /// <summary>The raw big-endian bytes; the span aliases this instance.</summary>
    [UnscopedRef]
    public Span<byte> AsSpan() => _val;

    public override readonly string ToString()
    {
        return HostValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public override int GetHashCode()
    {
        return HostValue.GetHashCode();
    }
};
