using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 4 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public struct BigEndianUInt32 : IInteropType<uint, BigEndianUInt32>
{
    private const int Length = sizeof(uint);

    private InlineArray4<byte> _val;

    public static BigEndianUInt32 FromHost(uint value)
    {
        var self = new BigEndianUInt32
        {
            HostValue = value
        };
        return self;
    }

    public uint HostValue
    {
        readonly get => BinaryPrimitives.ReadUInt32BigEndian(_val);
        init => BinaryPrimitives.WriteUInt32BigEndian(_val, value);
    }

    /// <summary>The raw big-endian bytes; the span aliases this instance.</summary>
    [UnscopedRef]
    public Span<byte> AsSpan() => _val;

    public static void Clear()
    {
    }

    public override readonly string ToString()
    {
        return HostValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public override int GetHashCode()
    {
        return HostValue.GetHashCode();
    }
};
