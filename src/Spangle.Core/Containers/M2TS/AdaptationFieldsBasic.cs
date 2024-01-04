using System.Runtime.InteropServices;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = Size, Size = Size)]
internal unsafe partial struct AdaptationFieldsBasic
{
    public const int Size = 2;

    [
        BitField(typeof(uint), "AdaptationFieldLength", 8, description: "Adaptation field length"),
        BitField(typeof(byte), "DiscontinuityIndicator", 1, description: "Discontinuity indicator"),
        BitField(typeof(byte), "RandomAccessIndicator", 1, description: "Random access indicator"),
        BitField(typeof(byte), "ESPriorityIndicator", 1, description: "Elementary stream priority indicator"),
        BitField(typeof(bool), "HasPCR", 1, description: "PCR flag"),
        BitField(typeof(bool), "HasOPCR", 1, description: "OPCR flag"),
        BitField(typeof(bool), "HasSplicingPoint", 1, description: "Splicing point flag"),
        BitField(typeof(bool), "HasTransportPrivateData", 1, description: "Transport private data flag"),
        BitField(typeof(bool), "HasAdaptationFieldExtension", 1, description: "Adaptation field extension flag"),
    ]
    private fixed byte _value[Size];
}
