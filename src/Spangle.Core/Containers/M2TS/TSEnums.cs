namespace Spangle.Containers.M2TS;

public enum TransportScramblingType : byte
{
    None  = 0b00,
    User1 = 0b01,
    User2 = 0b10,
    User3 = 0b11,
}

public enum AdaptationFieldType : byte
{
    Payload    = 0b01,
    Adaptation = 0b10,
    Both       = 0b11,
}

internal static class TSEnumExtensions
{
    public static bool HasPayload(this AdaptationFieldType type)
        => type is AdaptationFieldType.Payload or AdaptationFieldType.Both;

    public static bool HasAdaptation(this AdaptationFieldType type)
        => type is AdaptationFieldType.Adaptation or AdaptationFieldType.Both;
}
