using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 4 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public unsafe struct BigEndianUInt32 : IInteropType<uint, BigEndianUInt32>
{
    private const int Length = sizeof(uint);

    private fixed byte _val[Length];

    public static BigEndianUInt32 FromHost(uint value)
    {
        var self = new BigEndianUInt32
        {
            HostValue = value
        };
        return self;
    }

    public readonly uint HostValue
    {
        get
        {
            fixed (byte* p = _val)
            {
                return BinaryPrimitives.ReadUInt32BigEndian(new Span<byte>(p, Length));
            }
        }
        init
        {
            fixed (byte* p = _val)
            {
                BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(p, Length), value);
            }
        }
    }

    public readonly Span<byte> AsSpan()
    {
        fixed (byte* p = _val)
        {
            return new Span<byte>(p, Length);
        }
    }

    public void Clear()
    {
    }

    public override readonly string ToString()
    {
        return HostValue.ToString();
    }

    public override int GetHashCode()
    {
        return HostValue.GetHashCode();
    }
};
