using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Codecs;
using Spangle.Codecs.AVC;
using Spangle.Containers.Flv;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Transport.Rtmp;
using ZLogger;

namespace Spangle.Spinner;

/// <summary>
/// NAL file to Annex B spinner
/// </summary>
public sealed class FlvAVCToM2TSSpinner(RtmpReceiverContext context, PipeWriter anotherIntake, CancellationToken ct)
    : SpinnerBase<FlvAVCToM2TSSpinner>(anotherIntake, ct)
{
    private bool _hasSentAUD;

    private AVCDecoderConfigurationRecord? _avcConfig;

    private readonly ArrayBufferWriter<byte> _pesBuffer = new(1024);

    private static readonly ILogger<FlvAVCToM2TSSpinner> s_logger;

    static FlvAVCToM2TSSpinner()
    {
        s_logger = SpangleLogManager.GetLogger<FlvAVCToM2TSSpinner>();
    }

    public override async ValueTask SpinAsync()
    {
        while (!CancellationToken.IsCancellationRequested)
        {
            if (!context.VideoTagLengthQueue.TryDequeue(out int messageLength))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                continue;
            }
            var result = await IntakeReader.ReadAtLeastAsync(messageLength, CancellationToken);
            var buff = result.Buffer;

            ReadAndSendNext(ref buff, messageLength);

            IntakeReader.AdvanceTo(buff.Start);
            // TODO to PES
            await Outlet.FlushAsync(CancellationToken);
        }
    }

    private void ReadAndSendNext(ref ReadOnlySequence<byte> buff, int messageLength)
    {
        var headerBuff = buff.Slice(0, FlvAVCAdditionalHeader.Size);
        ref readonly var header = ref BufferMarshal.AsRefOrCopy<FlvAVCAdditionalHeader>(headerBuff);
        buff = buff.Slice(headerBuff.End);

        switch (header.PacketType)
        {
            case FlvAVCPacketType.SequenceHeader:
                ProcessAVCDecoderConfigurationRecord(ref buff);
                break;
            case FlvAVCPacketType.Nalu:
                ProcessNALU(ref buff, messageLength - FlvAVCAdditionalHeader.Size);
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

        int l = config.NumOfSequenceParameterSets;
        s_logger.ZLogTrace($"Number of SPS: {l}");
        const int spsLenSize = 2;
        for (var i = 0; i < l; i++)
        {
            var spsLenBuff = buff.Slice(0, spsLenSize);
            ushort spsLen = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(spsLenBuff).HostValue;
            var spsBuff = buff.Slice(spsLenSize, spsLen);
            NALAnnexB.WriteNALU(_pesBuffer, spsBuff);
            // _hasSentSPS = true;
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
            NALAnnexB.WriteNALU(_pesBuffer, ppsBuff);
            // _hasSentPPS = true;
            buff = buff.Slice(ppsBuff.End);
        }
    }

    private void ProcessNALU(ref ReadOnlySequence<byte> buff, int messageLength)
    {
        Debug.Assert(_avcConfig != null);

        int lengthSize = _avcConfig!.Value.LengthSize;
        s_logger.ZLogTrace($"NALU Length Size: {lengthSize}");

        while (messageLength > 0)
        {
            s_logger.ZLogTrace($"NALUs remaining: {messageLength}");
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

            s_logger.ZLogTrace($"NALU Length: {length}");

            var headerBuff = buff.Slice(0, 1);
            ref readonly var header = ref BufferMarshal.AsRefOrCopy<NALUnitHeader>(headerBuff);

            if (header.Type == NALUnitType.AUD)
            {
                _hasSentAUD = true;
            }
            else if (!_hasSentAUD)
            {
                _pesBuffer.Write(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x09 });
                _hasSentAUD = true;
            }

            NALAnnexB.WriteNALUIndicator(_pesBuffer);
            var writeBuff = _pesBuffer.GetSpan(length);
            var readBuff = buff.Slice(0, length);
            readBuff.CopyTo(writeBuff);

            _pesBuffer.Advance(length);
            buff = buff.Slice(readBuff.End);

            messageLength -= lengthSize + length;
            if (messageLength < 0)
            {
                throw new InvalidDataException("Invalid message length");
            }
        }
    }

    // private async ValueTask ReadMore(SequencePosition consumed, ref ReadOnlySequence<byte> buff)
    // {
    //     IntakeReader.AdvanceTo(consumed);
    //     var res = await IntakeReader.ReadAsync(CancellationToken);
    //     buff = res.Buffer;
    // }

    private void SendPES()
    {
        if (_pesBuffer.WrittenCount == 0)
        {
            return;
        }

        var pesBuff = _pesBuffer.WrittenSpan;

        _pesBuffer.Clear();
        // Send PES
        Outlet.Write(pesBuff);
    }
}
