using System.Runtime.InteropServices;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = Size, Size = Size)]
internal unsafe partial struct TSHeader
{
    public const int Size = 4;

    [
        // Header fields
        BitField(typeof(byte), "SyncByte", 8, description: "Sync byte"),
        BitField(typeof(byte), "TransportError", 1, description: "Transport error indicator"),
        BitField(typeof(byte), "PayloadUnitStart", 1, description: "Payload unit start indicator"),
        BitField(typeof(byte), "TransportPriority", 1, description: "Transport priority"),
        BitField(typeof(ushort), "PID", 13, description: "PID"),
        BitField(typeof(TransportScramblingType), "TransportScrambling", 2, description: "Transport scrambling control"),
        BitField(typeof(AdaptationFieldControlType), "AdaptationFieldControl", 2, description: "Adaptation field control"),
        BitField(typeof(byte), "ContinuityCounter", 4, description: "Continuity counter"),
    ]
    private fixed byte _value[Size];

    public enum TransportScramblingType : byte
    {
        None  = 0b00,
        User1 = 0b01,
        User2 = 0b10,
        User3 = 0b11,
    }

    [Flags]
    public enum AdaptationFieldControlType : byte
    {
        Payload = 0b01,
        AdaptationField = 0b10,
        // Both = 0b11,
    }
}

internal static class AdaptationFieldTypeExtensions
{
    public static bool HasPayload(this TSHeader.AdaptationFieldControlType type)
        => (type & TSHeader.AdaptationFieldControlType.Payload) != 0;
    public static bool HasAdaptationField(this TSHeader.AdaptationFieldControlType type)
        => (type & TSHeader.AdaptationFieldControlType.AdaptationField) != 0;
}
