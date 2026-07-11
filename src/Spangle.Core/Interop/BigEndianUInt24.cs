using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 3 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Length)]
public struct BigEndianUInt24 : IInteropType<uint, BigEndianUInt24>
{
    public const uint MaxValue = 0xFF_FF_FF;
    private const int Length = 3;

    private InlineArray3<byte> _val;

    public static BigEndianUInt24 FromHost(uint value)
    {
        var self = new BigEndianUInt24
        {
            HostValue = value
        };
        return self;
    }

    public uint HostValue
    {
        readonly get => (uint)(
            (_val[0] << 16)
            + (_val[1] << 8)
            + (_val[2] << 0)
        );
        init
        {
            if (value > MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"The value {value} is out of the range of 24-bit number");
            }
            _val[0] = (byte)(value >>> 16 & 0xFF);
            _val[1] = (byte)(value >>> 8 & 0xFF);
            _val[2] = (byte)(value >>> 0 & 0xFF);
        }
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
