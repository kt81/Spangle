using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.Codecs;
using Spangle.Codecs.AVC;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Transport.Rtmp;
using ZLogger;

namespace Spangle.Containers.Flv;

public static class FlvAVCReader
{
    private static readonly ILogger s_logger = SpangleLogManager.GetLogger(typeof(FlvAVCReader).FullName!);

    public static void ReadAndSendNext(RtmpReceiverContext context, ReadOnlySequence<byte> buff)
    {
        var writer = context.VideoWriter;

        var headerBuff = buff.Slice(0, FlvAVCAdditionalHeader.Size);
        ref readonly var header = ref BufferMarshal.AsRefOrCopy<FlvAVCAdditionalHeader>(headerBuff);
        buff = buff.Slice(headerBuff.End);

        if (header.PacketType == FlvAVCPacketType.SequenceHeader)
        {
            s_logger.ZLogDebug($"H264 sequence header");

            s_logger.ZLogTrace($"Parsing AVCDecoderConfigurationRecord fixed part");
            var config = new AVCDecoderConfigurationRecord();
            var configBuff = buff.Slice(0, AVCDecoderConfigurationRecord.Size);
            configBuff.CopyTo(config.AsSpan());
            buff = buff.Slice(configBuff.End);
            int lengthSize = config.LengthSize;

            s_logger.ZLogTrace($"Parsing SPS");

            int l = config.NumOfSequenceParameterSets;
            const int spsLenSize = 2;
            for (var i = 0; i < l; i++)
            {
                var spsLenBuff = buff.Slice(0, spsLenSize);
                ushort spsLen = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(spsLenBuff).HostValue;
                var spsBuff = buff.Slice(spsLenSize, spsLen);
                NALFileFormat.WriteNALU(writer, spsBuff, lengthSize);
                buff = buff.Slice(spsBuff.End);
            }

            s_logger.ZLogTrace($"Parsing PPS Size");
            var ppsSizeBuff = buff.Slice(0, 1);
            l = buff.FirstSpan[0];
            buff = buff.Slice(ppsSizeBuff.End);
            s_logger.ZLogTrace($"Parsing PPS");
            const int ppsLenSize = 2;
            for (var i = 0; i < l; i++)
            {
                var ppsLenBuff = buff.Slice(0, ppsLenSize);
                ushort ppsLen = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(ppsLenBuff).HostValue;
                var ppsBuff = buff.Slice(ppsLenSize, ppsLen);
                NALFileFormat.WriteNALU(writer, ppsBuff, lengthSize);
                buff = buff.Slice(ppsBuff.End);
            }
        }
        else if (header.PacketType == FlvAVCPacketType.Nalu)
        {
            while (buff.Length > 0)
            {
                var span = buff.FirstSpan;
                writer.Write(span);
                buff = buff.Slice(span.Length);
            }
        }
    }

}

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
