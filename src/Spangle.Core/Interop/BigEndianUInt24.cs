using System.Runtime.InteropServices;

namespace Spangle.Interop;

/// <summary>
/// Struct mapping for 3 bytes number of Big Endian
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Length)]
public unsafe struct BigEndianUInt24 : IBigEndianUInt<BigEndianUInt24>
{
    private const int Length = 3;
    private const int MaxValue = 0xFF_FF_FF;
    
    private fixed byte _val[Length];

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
        get
        {
            return (uint)(
                (_val[0] << 16) 
                + (_val[1] << 8) 
                + (_val[2] << 0)
                );
        }
        set
        {
            if (value > MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"The value {value} is out of the range of 24-bit number");
            }
            _val[0] = (byte)(value >> 16 & 0xFF);
            _val[1] = (byte)(value >> 8 & 0xFF);
            _val[2] = (byte)(value >> 0 & 0xFF);
        }
    }

    public override string ToString()
    {
        return HostValue.ToString();
    }
};
