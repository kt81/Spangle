namespace Spangle.IO.Interop;

public interface IBigEndianUInt
{
    /// <summary>
    /// Get or Set the value as host primitive
    /// </summary>
    uint HostValue { get; init; }
}

public interface IBigEndianUInt<out TSelf> : IBigEndianUInt where TSelf : struct, IBigEndianUInt
{
    /// <summary>
    /// Create instance from the value of host primitive
    /// </summary>
    /// <param name="hostValue"></param>
    /// <returns></returns>
    static abstract TSelf FromHost(uint hostValue);
}
