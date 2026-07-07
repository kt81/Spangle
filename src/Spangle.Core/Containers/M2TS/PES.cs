using System.Buffers;
using System.Runtime.InteropServices;
using Spangle.Interop;
using Spangle.LusterBits;

namespace Spangle.Containers.M2TS;

public struct PESHeader
{
    public readonly BigEndianUInt24 PacketStartCodePrefix = BigEndianUInt24.FromHost(1);
    public          byte            StreamId;

    public BigEndianUInt16 PESPacketLength;

    public OptionalPESHeader OptionalHeader;

    public PESHeader()
    {
    }
}

[LusterCharm]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public unsafe partial struct OptionalPESHeader
{
    public const int Size = 3;

    [
        BitField(typeof(byte), "MarkerBits", 2),
        BitField(typeof(byte), "ScramblingControl", 2),
        BitField(typeof(byte), "Priority", 1),
        BitField(typeof(byte), "DataAlignmentIndicator", 1),
        BitField(typeof(byte), "CopyRight", 1),
        BitField(typeof(byte), "OriginalOrCopy", 1),
        //
        BitField(typeof(byte), "PTS_DTSFlags", 2),
        BitField(typeof(byte), "ESCRFlag", 1),
        BitField(typeof(byte), "ESRateFlag", 1),
        BitField(typeof(byte), "DSMTrickModeFlag", 1),
        BitField(typeof(byte), "AdditionalCopyInfoFlag", 1),
        BitField(typeof(byte), "CRCFlag", 1),
        BitField(typeof(byte), "ExtensionFlag", 1),
        //
        BitField(typeof(byte), "PESHeaderLength", 8)
        // Optional fields
        // Stuffing bytes
    ]
    private fixed byte _value[3];
}

public static class PESWriter
{
    // TODO Not implemented yet. Packetize the ES payload into PES / 188-byte TS packets and write them to the outlet.
    public static void WritePES(
        ReadOnlySpan<byte> payload,
        int pid,
        int continuityCounter,
        bool omitsPesLength,
        byte streamId,
        ulong? pts,
        ulong? dts,
        IBufferWriter<byte> outlet)
    {
    }
}
