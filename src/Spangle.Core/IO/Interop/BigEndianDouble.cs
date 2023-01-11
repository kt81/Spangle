using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Spangle.IO.Interop;

/// <summary>
/// Struct mapping for 8 bytes double precision floating point value of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public unsafe struct BigEndianDouble : IInteropType<double, BigEndianDouble>
{
    private const int Length = sizeof(double);

    private fixed byte _val[Length];

    public static BigEndianDouble FromHost(double value)
    {
        var self = new BigEndianDouble()
        {
            HostValue = value
        };
        return self;
    }

    public readonly double HostValue
    {
        get
        {
            fixed (byte* p = _val)
            {
                return BinaryPrimitives.ReadDoubleBigEndian(new Span<byte>(p, Length));
            }
        }
        init
        {
            fixed (byte* p = _val)
            {
                BinaryPrimitives.WriteDoubleBigEndian(new Span<byte>(p, Length), value);
            }
        }
    }

    public override readonly string ToString()
    {
        // ReSharper disable once SpecifyACultureInStringConversionExplicitly
        return HostValue.ToString();
    }

    public readonly string ToString([StringSyntax(StringSyntaxAttribute.NumericFormat)] string? format)
    {
        return HostValue.ToString(format);
    }

    public override int GetHashCode()
    {
        return HostValue.GetHashCode();
    }
}
