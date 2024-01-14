using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Containers.Flv;
using Spangle.Interop;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class Video : IReadStateAction
{
    private static readonly ILogger<Video> s_logger = SpangleLogManager.GetLogger<Video>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;

        await using var enumerator = ReadHelper.ReadChunkedMessageBody(context).GetAsyncEnumerator(ct);
        await enumerator.MoveNextAsync();
        var buff = enumerator.Current;

        var packetType = ReadVideoTagHeader(context, ref buff, enumerator);
        ReadVideoTagBody(context, ref buff, enumerator, packetType);
        await context.VideoWriter.FlushAsync(ct);

        reader.AdvanceTo(buff.End);

        // Read all chunks
        while (await enumerator.MoveNextAsync())
        {
            buff = enumerator.Current;
            reader.AdvanceTo(buff.End);
        }

        // switch (control.Codec.ToInternal())
        // {
        //     case VideoCodec.H264:
        // }
        // buff.ToArray().DumpHex(s_logger.ZLogDebug);
        // Advance to last buffer's End position

        context.SetNext<ReadChunkHeader>();
    }

    private static FlvVideoPacketType ReadVideoTagHeader(RtmpReceiverContext context, ref ReadOnlySequence<byte> buff,
        IAsyncEnumerator<ReadOnlySequence<byte>> enumerator)
    {
        // Parse control
        var ctrl = new FlvVideoControl(buff.FirstSpan[0]);
        context.IsEnhanced = ctrl.IsEnhanced; // aka. IsExHeader
        s_logger.ZLogDebug(
            $$"""FlvVideoControl {frameType:{{ctrl.FrameType}}, codec:{{ctrl.Codec}}, isEnhanced:{{context.IsEnhanced}}}""");

        FlvVideoPacketType packetType;
        SequencePosition endPos;

        if (context.IsEnhanced)
        {
            // The field of CodecID is treated as PacketType if IsExHeader == true
            packetType = ctrl.VideoPacketType;
            var fourCCBuff = buff.Slice(buff.GetPosition(1), 4);
            var fourCCVal = new BigEndianUInt32();
            fourCCBuff.CopyTo(fourCCVal.AsSpan());
            context.VideoCodec = FlvVideoCodecExtensions.ParseToInternal(fourCCVal.HostValue);
            endPos = fourCCBuff.End;
        }
        else
        {
            // IsExHeader == false
            context.VideoCodec = ctrl.Codec.ToInternal();
            // The value is invalid. It must be ignored.
            packetType = FlvVideoPacketType.PacketTypeSequenceStart;
            endPos = buff.GetPosition(1);
        }

        buff = buff.Slice(endPos);
        return packetType;
    }

    private static void ReadVideoTagBody(RtmpReceiverContext context, ref ReadOnlySequence<byte> buff,
        IAsyncEnumerator<ReadOnlySequence<byte>> enumerator, FlvVideoPacketType packetType)
    {
        if (packetType == FlvVideoPacketType.PacketTypeMetadata)
        {
            // OBS does not send this packet
            s_logger.ZLogDebug($"Meta data");
        }
        else if (packetType == FlvVideoPacketType.PackoetTypeSequenceEnd)
        {
            s_logger.ZLogDebug($"End of sequence");
        }

        if (context.VideoCodec == VideoCodec.H264)
        {
            FlvAVCReader.ReadAndSendNext(context, buff);
        }

        if (context.VideoCodec == VideoCodec.AV1)
        {
            if (packetType == FlvVideoPacketType.PacketTypeSequenceStart)
            {
                s_logger.ZLogDebug($"AV1 sequence start");
                // TODO read config
            }
            else if (packetType == FlvVideoPacketType.PacketTypeMPEG2TSSequenceStart)
            {
                // OBS does not send this packet
                s_logger.ZLogDebug($"AV1 mpeg2ts sequence start");
            }
            else if (packetType == FlvVideoPacketType.PacketTypeCodedFrames)
            {
                s_logger.ZLogDebug($"AV1 coded frames");
                // TODO parse coded frames
            }
        }

        if (context.VideoCodec == VideoCodec.VP9)
        {
            if (packetType == FlvVideoPacketType.PacketTypeSequenceStart)
            {
                s_logger.ZLogDebug($"VP9 sequence start");
                // TODO read config
            }
            else if (packetType == FlvVideoPacketType.PacketTypeCodedFrames)
            {
                s_logger.ZLogDebug($"VP9 coded frames");
                // TODO parse coded frames
            }
        }

        if (context.VideoCodec == VideoCodec.H265)
        {
            if (packetType == FlvVideoPacketType.PacketTypeSequenceStart)
            {
                s_logger.ZLogDebug($"H265 sequence start");
                // TODO read config [ ISO 14496-15, 8.3.3.1.2 for the description of HEVCDecoderConfigurationRecord]
            }
            else if (packetType is FlvVideoPacketType.PacketTypeCodedFrames or FlvVideoPacketType.PacketTypeCodedFramesX)
            {
                if (packetType == FlvVideoPacketType.PacketTypeCodedFrames)
                {
                    s_logger.ZLogDebug($"H265 coded frames");
                    // TODO read composition time offset [  See ISO 14496-12, 8.15.3 for an explanation of composition times ]
                }
                else if (packetType == FlvVideoPacketType.PacketTypeCodedFramesX)
                {
                    s_logger.ZLogDebug($"H265 coded framesX");
                    // TODO treat as 0 for composition time offset
                }
                // TODO parse HEVC NAL units
            }
        }
    }
}
