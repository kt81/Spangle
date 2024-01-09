using System.Buffers;
using Spangle.Interop;

namespace Spangle.Codecs;

public static unsafe class NALFileFormatReader
{
    public static uint ReadNALULength(ref ReadOnlySequence<byte> buff, int lengthSize)
    {
        var lenBuff = buff.Slice(0, lengthSize);
        uint length;
        switch (lengthSize)
        {
            case 1:
                byte numByte;
                lenBuff.CopyTo(new Span<byte>(&numByte, lengthSize));
                length = numByte;
                break;
            case 2:
                BigEndianUInt16 numShort;
                lenBuff.CopyTo(new Span<byte>(&numShort, lengthSize));
                length = numShort.HostValue;
                break;
            case 4:
                BigEndianUInt32 numInt;
                lenBuff.CopyTo(new Span<byte>(&numInt, lengthSize));
                length = numInt.HostValue;
                break;
            default:
                throw new InvalidDataException();
        }

        buff = buff.Slice(lenBuff.End);
        return length;
    }
}
