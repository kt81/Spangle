using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 8 bytes double precision floating point value of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public struct BigEndianDouble : IInteropType<double, BigEndianDouble>
{
    private const int Length = sizeof(double);

    private InlineArray8<byte> _val;

    public static BigEndianDouble FromHost(double value)
    {
        var self = new BigEndianDouble
        {
            HostValue = value
        };
        return self;
    }

    public double HostValue
    {
        readonly get => BinaryPrimitives.ReadDoubleBigEndian(_val);
        init => BinaryPrimitives.WriteDoubleBigEndian(_val, value);
    }

    /// <summary>The raw big-endian bytes; the span aliases this instance.</summary>
    [UnscopedRef]
    public Span<byte> AsSpan() => _val;

    public override readonly string ToString()
    {
        return HostValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public readonly string ToString([StringSyntax(StringSyntaxAttribute.NumericFormat)] string? format)
    {
        return HostValue.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    public override int GetHashCode()
    {
        return HostValue.GetHashCode();
    }
}
