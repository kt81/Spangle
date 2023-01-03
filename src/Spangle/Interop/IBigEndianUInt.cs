namespace Spangle.Interop;

public interface IBigEndianUInt
{
    uint HostValue { get; set; }
}

public interface IBigEndianUInt<out TSelf> : IBigEndianUInt where TSelf : struct, IBigEndianUInt
{
    static abstract TSelf FromHost(uint hostValue);
}
