namespace Spangle.Interop;

public interface IInteropType<THostType>
{
    /// <summary>
    /// Get or Set the value as host primitive
    /// </summary>
    THostType HostValue { get; init; }
}

public interface IInteropType<THostType, out TSelf> : IInteropType<THostType> where TSelf : struct, IInteropType<THostType>
{
    /// <summary>
    /// Create instance from the value of host primitive
    /// </summary>
    /// <param name="hostValue"></param>
    /// <returns></returns>
    static abstract TSelf FromHost(THostType hostValue);
}
