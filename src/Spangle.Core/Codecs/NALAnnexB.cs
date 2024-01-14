using System.Buffers;
using Spangle.Interop;
using Spangle.IO;

namespace Spangle.Codecs;

public static unsafe class NALAnnexB
{
    public const int NALUIndicatorSize = 3;

    public static int WriteNALU(IBufferWriter<byte> buff, ReadOnlySequence<byte> nalu)
    {
        int l = WriteNALUIndicator(buff);
        nalu.CopyTo(buff.GetSpan((int)nalu.Length));
        buff.Advance((int)nalu.Length);
        return l + (int)nalu.Length;
    }

    private static int WriteNALUIndicator(IBufferWriter<byte> buff)
    {
        var span = buff.GetSpan(NALUIndicatorSize);
        span[0] = 0x00;
        span[1] = 0x00;
        span[2] = 0x01;
        buff.Advance(NALUIndicatorSize);
        return NALUIndicatorSize;
    }
}
