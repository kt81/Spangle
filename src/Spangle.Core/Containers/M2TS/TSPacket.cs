using System.Runtime.InteropServices;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

/// <summary>
/// A 188-byte transport stream packet, read as a chain of conditional segments:
/// the presence of each optional part shifts the offsets of everything after it.
/// Later adaptation field extras (splicing point, private data, extensions) are
/// not mapped yet; add further segments when a consumer needs them.
/// </summary>
[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
internal unsafe partial struct TSPacket
{
    public const int MaxPayloadSize = 184;
    public const int Size           = TSHeader.Size + MaxPayloadSize;

    [
        Segment(typeof(TSHeader), "Header", Description = "Packet header"),
        Segment(typeof(AdaptationFieldsBasic), "AdaptationFields",
            If = "Header.AdaptationFieldControl.HasAdaptationField()",
            Description = "Adaptation field length and flags"),
        Segment(typeof(PCR), "PCR", If = "AdaptationFields.HasPCR",
            Description = "Program clock reference"),
        Segment(typeof(PCR), "OPCR", If = "AdaptationFields.HasOPCR",
            Description = "Original program clock reference"),
    ]
    private fixed byte _value[Size];
}
