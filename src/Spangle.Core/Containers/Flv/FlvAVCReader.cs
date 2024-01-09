using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.Codecs.AVC;
using Spangle.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Containers.Flv;

public static unsafe class FlvAVCReader
{
    private static readonly ILogger s_logger = SpangleLogManager.GetLogger(typeof(FlvAVCReader).FullName!);

    public static void Read(ReadOnlySequence<byte> buff)
    {
        var header = new FlvAVCAdditionalHeader();
        var headerBuff = buff.Slice(0, FlvAVCAdditionalHeader.Size);
        headerBuff.CopyTo(new Span<byte>(&header, FlvAVCAdditionalHeader.Size));
        buff = buff.Slice(headerBuff.End);
        if (header.PacketType == FlvAVCPacketType.SequenceHeader)
        {
            s_logger.ZLogDebug($"H264 sequence header");
            s_logger.ZLogTrace($"Parsing AVCDecoderConfigurationRecord");
            var config = new AVCDecoderConfigurationRecordFixedPart();
            var configBuff = buff.Slice(0, AVCDecoderConfigurationRecordFixedPart.Size);
            configBuff.CopyTo(config.AsSpan());
            buff = buff.Slice(configBuff.End);

            if (config.NumOfSequenceParameterSets > 0)
            {
                s_logger.ZLogTrace($"Parsing SPS");
                // var spsBuff = buff.Slice(0, config.SequenceParameterSetLength.HostValue);
                // var sps = new AVCSequenceParameterSet();
                // spsBuff.CopyTo(sps.AsSpan());
                // buff = buff.Slice(spsBuff.End);
            }
            // TODO read config
        }
        else if (header.PacketType == FlvAVCPacketType.Nalu)
        {
            s_logger.ZLogDebug($"H264 nalu");
            // TODO parse NAL units
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
