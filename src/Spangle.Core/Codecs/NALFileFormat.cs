using System.Buffers;
using System.Runtime.InteropServices;
using Spangle.Interop;
using Spangle.IO;

namespace Spangle.Codecs;

public static unsafe class NALFileFormat
{
    public static int WriteNALU(IBufferWriter<byte> buff, ReadOnlySequence<byte> nalu)
    {
        int lengthSize = WriteNALUIndicator(buff, (int)nalu.Length);
        nalu.CopyTo(buff.GetSpan((int)nalu.Length));
        buff.Advance((int)nalu.Length);
        return lengthSize + (int)nalu.Length;
    }

    private static int WriteNALUIndicator(IBufferWriter<byte> buff, int naluLength)
    {
        // We always treat the NALU length size as 4 internally in this application.
        const int lengthSize = 4;
        var lenSpan = buff.GetSpan(lengthSize);
        BigEndianUInt32.FromHost((uint)naluLength).AsSpan().CopyTo(lenSpan);
        buff.Advance(lengthSize);
        return lengthSize;
    }

    // FileFormat
    public static uint ReadNALULength(ref ReadOnlySequence<byte> buff, int lengthSize)
    {
        var lenBuff = buff.Slice(0, lengthSize);
        uint length = lengthSize switch
        {
            1 => BufferMarshal.As<byte>(lenBuff),
            2 => BufferMarshal.As<BigEndianUInt16>(lenBuff).HostValue,
            4 => BufferMarshal.As<BigEndianUInt32>(lenBuff).HostValue,
            _ => throw new InvalidDataException(),
        };

        buff = buff.Slice(lenBuff.End);
        return length;
    }
}
