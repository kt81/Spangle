using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 4 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = Length, Size = Length)]
public unsafe struct BigEndianUInt32 : IBigEndianUInt<BigEndianUInt32>
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
    
    public uint HostValue
    {
        get
        {
            fixed (byte* p = _val)
            {
                return BinaryPrimitives.ReadUInt32BigEndian(new Span<byte>(p, Length));
            }
        }
        set
        {
            fixed (byte* p = _val)
            {
                BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(p, Length), value);
            }
        }
    }

    public override string ToString()
    {
        return HostValue.ToString();
    }
};
