using System.Buffers;
using Spangle.Interop;

namespace Spangle.Codecs.AVC;

/// <summary>
/// Reader for AVCDecoderConfigurationRecord (avcC, ISO/IEC 14496-15 5.3.3.1).
/// Extracts the NALU length size and the parameter set NALUs (SPS/PPS).
/// </summary>
internal static class AVCDecoderConfigurationRecordReader
{
    private static readonly byte[] s_startCode4 = [0x00, 0x00, 0x00, 0x01];

    /// <summary>
    /// Parses the record, writing all parameter set NALUs in Annex B form (4-byte start codes)
    /// into <paramref name="parameterSetsOut"/>.
    /// </summary>
    /// <returns>The NALU length size (1, 2 or 4) used by the stream</returns>
    public static int Parse(ReadOnlySequence<byte> buff, IBufferWriter<byte> parameterSetsOut)
    {
        var config = new AVCDecoderConfigurationRecord();
        var configBuff = buff.Slice(0, AVCDecoderConfigurationRecord.Size);
        configBuff.CopyTo(config.AsSpan());
        buff = buff.Slice(configBuff.End);

        int numOfSps = config.NumOfSequenceParameterSets;
        const int lenSize = 2;
        for (var i = 0; i < numOfSps; i++)
        {
            ushort len = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(buff.Slice(0, lenSize)).HostValue;
            WriteNalu(parameterSetsOut, buff.Slice(lenSize, len));
            buff = buff.Slice(lenSize + len);
        }

        Span<byte> one = stackalloc byte[1];
        buff.Slice(0, 1).CopyTo(one);
        int numOfPps = one[0];
        buff = buff.Slice(1);
        for (var i = 0; i < numOfPps; i++)
        {
            ushort len = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(buff.Slice(0, lenSize)).HostValue;
            WriteNalu(parameterSetsOut, buff.Slice(lenSize, len));
            buff = buff.Slice(lenSize + len);
        }

        return config.LengthSize;
    }

    private static void WriteNalu(IBufferWriter<byte> writer, ReadOnlySequence<byte> nalu)
    {
        // SPS/PPS require the zero_byte before the start code (Annex B)
        writer.Write(s_startCode4);
        nalu.CopyTo(writer.GetSpan((int)nalu.Length));
        writer.Advance((int)nalu.Length);
    }
}
