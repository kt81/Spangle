using System.Runtime.InteropServices;
using Spangle.Interop;

namespace Spangle.Containers.Flv;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public struct FlvAVCAdditionalHeader
{
    public const int Size = 4;

    public FlvAVCPacketType PacketType;
    public BigEndianUInt24  CompositionTime;
}

public enum FlvAVCPacketType : byte
{
    SequenceHeader = 0,
    Nalu           = 1,
    EndOfSequence  = 2,
}
