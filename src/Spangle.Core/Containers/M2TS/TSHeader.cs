using System.Runtime.InteropServices;

namespace Spangle.Containers.M2TS;

[StructLayout(LayoutKind.Sequential, Pack = Size, Size = Size)]
internal unsafe struct TSHeader
{
    public const int Size = 4;

    private fixed byte _value[Size];

    // Header fields
    public readonly byte SyncByte => _value[0];
    public readonly byte TransportError => (byte)(_value[1] >>> 7);
    public readonly byte PayloadUnitStart => (byte)((_value[1] & 0x40) >>> 6);
    public readonly byte TransportPriority => (byte)((_value[1] & 0x20) >>> 5);
    public readonly ushort PID => (ushort)(((_value[1] & 0x1F) << 8) + _value[2]);
    public readonly TransportScramblingType TransportScrambling => (TransportScramblingType)(_value[3] >>> 6);
    public readonly AdaptationFieldType AdaptationFieldControl => (AdaptationFieldType)((_value[3] & 0x30) >>> 4);
    public readonly byte ContinuityCounter => (byte)(_value[3] & 0x0F);

}
