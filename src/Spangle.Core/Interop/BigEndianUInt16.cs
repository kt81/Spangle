using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 2 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public unsafe struct BigEndianUInt16 : IInteropType<ushort, BigEndianUInt16>
{
    private const int Length = sizeof(ushort);

    private fixed byte _val[Length];

    public static BigEndianUInt16 FromHost(ushort value)
    {
        var self = new BigEndianUInt16
        {
            HostValue = value
        };
        return self;
    }

    public readonly ushort HostValue
    {
        get
        {
            fixed (byte* p = _val)
            {
                return BinaryPrimitives.ReadUInt16BigEndian(new Span<byte>(p, Length));
            }
        }
        init
        {
            fixed (byte* p = _val)
            {
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(p, Length), value);
            }
        }
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
