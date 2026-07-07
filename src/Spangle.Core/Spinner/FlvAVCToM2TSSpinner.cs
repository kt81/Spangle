using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Codecs;
using Spangle.Codecs.AVC;
using Spangle.Containers.Flv;
using Spangle.Containers.M2TS;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Transport.Rtmp;
using ZLogger;

namespace Spangle.Spinner;

/// <summary>
/// Converts FLV/AVC video tags into an MPEG-2 TS stream (PAT/PMT + PES with Annex B payload).
/// </summary>
public sealed class FlvAVCToM2TSSpinner(RtmpReceiverContext context, PipeWriter anotherIntake, CancellationToken ct)
    : SpinnerBase<FlvAVCToM2TSSpinner>(anotherIntake, ct)
{
    private static readonly byte[] s_accessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];
    private static readonly byte[] s_startCode4 = [0x00, 0x00, 0x00, 0x01];

    private ulong? _dts;
    private ulong? _pts;
    private bool   _isKeyFrame;

    private AVCDecoderConfigurationRecord? _avcConfig;

    /// <summary>SPS/PPS in Annex B form, re-injected at every keyframe for segment independence.</summary>
    private byte[]? _parameterSets;

    private readonly ArrayBufferWriter<byte> _esBuffer = new(4096);
    private readonly M2TSWriter _tsWriter = new();

    private static readonly ILogger<FlvAVCToM2TSSpinner> s_logger;

    static FlvAVCToM2TSSpinner()
    {
        s_logger = SpangleLogManager.GetLogger<FlvAVCToM2TSSpinner>();
    }

    public override async ValueTask SpinAsync()
    {
        while (!CancellationToken.IsCancellationRequested)
        {
            if (!context.VideoReaderContexts.TryDequeue(out var readerContext))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                continue;
            }

            var result = await IntakeReader.ReadAtLeastAsync(readerContext.MessageLength, CancellationToken);
            var buff = result.Buffer;

            ReadNext(ref buff, readerContext);

            IntakeReader.AdvanceTo(buff.Start);

            SendPes();
            await Outlet.FlushAsync(CancellationToken);
        }
    }

    private void ReadNext(ref ReadOnlySequence<byte> buff, VideoReaderContext readerContext)
    {
        var headerBuff = buff.Slice(0, FlvAVCAdditionalHeader.Size);
        ref readonly var header = ref BufferMarshal.AsRefOrCopy<FlvAVCAdditionalHeader>(headerBuff);
        buff = buff.Slice(headerBuff.End);

        // CompositionTime is SI24
        var compositionTime = (int)header.CompositionTime.HostValue;
        if ((compositionTime & 0x800000) != 0)
        {
            compositionTime -= 0x1000000;
        }

        // timestamp is in milliseconds; pts and dts are in 90kHz units
        long ptsMs = readerContext.Timestamp + compositionTime;
        if (ptsMs < 0)
        {
            ptsMs = 0;
        }
        _dts = readerContext.Timestamp * 90ul;
        _pts = (ulong)ptsMs * 90ul;
        _isKeyFrame = readerContext.IsKeyFrame;

        switch (header.PacketType)
        {
            case FlvAVCPacketType.SequenceHeader:
                ProcessAVCDecoderConfigurationRecord(ref buff);
                break;
            case FlvAVCPacketType.Nalu:
                readerContext.MessageLength -= FlvAVCAdditionalHeader.Size;
                BuildAccessUnit(ref buff, readerContext);
                break;
            case FlvAVCPacketType.EndOfSequence:
                break;
            default:
                throw new InvalidDataException($"Invalid FlvAVCPacketType: {header.PacketType}");
        }
    }

    private void ProcessAVCDecoderConfigurationRecord(ref ReadOnlySequence<byte> buff)
    {
        s_logger.ZLogTrace($"Parsing AVCDecoderConfigurationRecord fixed part");
        var config = new AVCDecoderConfigurationRecord();
        var configBuff = buff.Slice(0, AVCDecoderConfigurationRecord.Size);
        configBuff.CopyTo(config.AsSpan());
        buff = buff.Slice(configBuff.End);
        _avcConfig = config;

        var psBuffer = new ArrayBufferWriter<byte>(256);

        int l = config.NumOfSequenceParameterSets;
        s_logger.ZLogTrace($"Number of SPS: {l}");
        const int spsLenSize = 2;
        for (var i = 0; i < l; i++)
        {
            var spsLenBuff = buff.Slice(0, spsLenSize);
            ushort spsLen = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(spsLenBuff).HostValue;
            var spsBuff = buff.Slice(spsLenSize, spsLen);
            WriteNaluWithStartCode4(psBuffer, spsBuff);
            buff = buff.Slice(spsBuff.End);
        }

        var ppsSizeBuff = buff.Slice(0, 1);
        l = buff.FirstSpan[0];
        buff = buff.Slice(ppsSizeBuff.End);
        s_logger.ZLogTrace($"Number of PPS: {l}");
        const int ppsLenSize = 2;
        for (var i = 0; i < l; i++)
        {
            var ppsLenBuff = buff.Slice(0, ppsLenSize);
            ushort ppsLen = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(ppsLenBuff).HostValue;
            var ppsBuff = buff.Slice(ppsLenSize, ppsLen);
            WriteNaluWithStartCode4(psBuffer, ppsBuff);
            buff = buff.Slice(ppsBuff.End);
        }

        _parameterSets = psBuffer.WrittenSpan.ToArray();
    }

    private static void WriteNaluWithStartCode4(IBufferWriter<byte> writer, ReadOnlySequence<byte> nalu)
    {
        // SPS/PPS require the zero_byte before the start code (Annex B)
        writer.Write(s_startCode4);
        nalu.CopyTo(writer.GetSpan((int)nalu.Length));
        writer.Advance((int)nalu.Length);
    }

    private void BuildAccessUnit(ref ReadOnlySequence<byte> buff, VideoReaderContext readerContext)
    {
        Debug.Assert(_avcConfig != null);

        int lengthSize = _avcConfig!.Value.LengthSize;
        int messageLength = readerContext.MessageLength;

        _esBuffer.Clear();
        _esBuffer.Write(s_accessUnitDelimiter);
        if (_isKeyFrame && _parameterSets is not null)
        {
            _esBuffer.Write(_parameterSets);
        }

        while (messageLength > 0)
        {
            var lengthBuff = buff.Slice(0, lengthSize);

            int length;
            switch (lengthSize)
            {
                case 1:
                    length = lengthBuff.FirstSpan[0];
                    break;
                case 2:
                    {
                        ref readonly var tmp = ref BufferMarshal.AsRefOrCopy<BigEndianUInt16>(lengthBuff);
                        length = tmp.HostValue;
                        break;
                    }
                case 4:
                    {
                        ref readonly var tmp = ref BufferMarshal.AsRefOrCopy<BigEndianUInt32>(lengthBuff);
                        length = (int)tmp.HostValue;
                        break;
                    }
                default:
                    // Not to be reached
                    throw new InvalidDataException();
            }

            buff = buff.Slice(lengthBuff.End);

            var headerBuff = buff.Slice(0, 1);
            ref readonly var header = ref BufferMarshal.AsRefOrCopy<NALUnitHeader>(headerBuff);

            if (header.Type == NALUnitType.AUD)
            {
                // We prepend our own AUD; drop the source's one
                buff = buff.Slice(length);
                messageLength -= lengthSize + length;
                continue;
            }

            NALAnnexB.WriteNALUIndicator(_esBuffer);
            var writeBuff = _esBuffer.GetSpan(length);
            var readBuff = buff.Slice(0, length);
            readBuff.CopyTo(writeBuff);

            _esBuffer.Advance(length);
            buff = buff.Slice(readBuff.End);

            messageLength -= lengthSize + length;
            if (messageLength < 0)
            {
                throw new InvalidDataException("Invalid message length");
            }
        }
    }

    private void SendPes()
    {
        if (_esBuffer.WrittenCount == 0)
        {
            return;
        }

        if (_isKeyFrame)
        {
            _tsWriter.WriteProgramTables(Outlet);
        }

        // Omit DTS when equal to PTS (no B-frames)
        ulong? dts = _dts == _pts ? null : _dts;
        _tsWriter.WritePes(Outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            _esBuffer.WrittenSpan, _pts, dts, _isKeyFrame, withPcr: true);

        _esBuffer.Clear();
    }
}
