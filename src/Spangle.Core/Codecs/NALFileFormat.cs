using System.Buffers;
using System.Runtime.InteropServices;
using Spangle.Interop;
using Spangle.IO;

namespace Spangle.Codecs;

public static unsafe class NALFileFormat
{
    public static int WriteNALU(IBufferWriter<byte> buff, ReadOnlySequence<byte> nalu, int lengthSize)
    {
        WriteNALUIndicator(buff, (int)nalu.Length, lengthSize);
        nalu.CopyTo(buff.GetSpan((int)nalu.Length));
        buff.Advance((int)nalu.Length);
        return lengthSize + (int)nalu.Length;
    }

    private static void WriteNALUIndicator(IBufferWriter<byte> buff, int naluLength, int lengthSize)
    {
        var lenSpan = buff.GetSpan(lengthSize);
        switch (lengthSize)
        {
            case 1:
                lenSpan[0] = (byte)naluLength;
                break;
            case 2:
                BigEndianUInt16.FromHost((ushort)naluLength).AsSpan().CopyTo(lenSpan);
                break;
            case 4:
                BigEndianUInt32.FromHost((uint)naluLength).AsSpan().CopyTo(lenSpan);
                break;
            default:
                throw new InvalidDataException();
        }

        buff.Advance(lengthSize);
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
