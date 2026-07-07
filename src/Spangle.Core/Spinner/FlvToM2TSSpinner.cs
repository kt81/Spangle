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
/// Converts framed FLV media (AVC video and AAC audio) into an MPEG-2 TS stream
/// (PAT/PMT + PES). Consumes <see cref="MediaFrameHeader"/>-framed data from the intake pipe.
/// </summary>
public sealed class FlvToM2TSSpinner(RtmpReceiverContext context, PipeWriter anotherIntake, CancellationToken ct)
    : SpinnerBase<FlvToM2TSSpinner>(anotherIntake, ct)
{
    private static readonly byte[] s_accessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];
    private static readonly byte[] s_startCode4 = [0x00, 0x00, 0x00, 0x01];

    private AVCDecoderConfigurationRecord? _avcConfig;

    /// <summary>SPS/PPS in Annex B form, re-injected at every keyframe for segment independence.</summary>
    private byte[]? _parameterSets;

    /// <summary>ADTS header template built from the AudioSpecificConfig; bytes 3..5 are patched per frame.</summary>
    private byte[]? _adtsTemplate;

    private readonly ArrayBufferWriter<byte> _esBuffer = new(4096);
    private readonly M2TSWriter _tsWriter = new();

    private static readonly ILogger<FlvToM2TSSpinner> s_logger;

    static FlvToM2TSSpinner()
    {
        s_logger = SpangleLogManager.GetLogger<FlvToM2TSSpinner>();
    }

    public override async ValueTask SpinAsync()
    {
        try
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                var result = await IntakeReader.ReadAtLeastAsync(MediaFrameHeader.Size, CancellationToken);
                if (result.Buffer.Length < MediaFrameHeader.Size)
                {
                    break; // intake completed
                }
                var headerBuff = result.Buffer.Slice(0, MediaFrameHeader.Size);
                var header = BufferMarshal.AsRefOrCopy<MediaFrameHeader>(headerBuff);
                IntakeReader.AdvanceTo(headerBuff.End);

                if (header.Length <= 0)
                {
                    continue;
                }

                result = await IntakeReader.ReadAtLeastAsync(header.Length, CancellationToken);
                if (result.Buffer.Length < header.Length)
                {
                    break; // intake completed halfway; drop the partial frame
                }
                var payload = result.Buffer.Slice(0, header.Length);

                switch (header.Kind)
                {
                    case MediaFrameKind.Video:
                        ProcessVideoFrame(in header, payload);
                        break;
                    case MediaFrameKind.Audio:
                        ProcessAudioFrame(in header, payload);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown media frame kind: {header.Kind}");
                }

                IntakeReader.AdvanceTo(payload.End);
                await Outlet.FlushAsync(CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await IntakeReader.CompleteAsync();
            await Outlet.CompleteAsync();
        }
    }

    #region Video (AVC)

    private void ProcessVideoFrame(in MediaFrameHeader frameHeader, ReadOnlySequence<byte> payload)
    {
        var headerBuff = payload.Slice(0, FlvAVCAdditionalHeader.Size);
        ref readonly var avcHeader = ref BufferMarshal.AsRefOrCopy<FlvAVCAdditionalHeader>(headerBuff);
        var body = payload.Slice(headerBuff.End);

        switch (avcHeader.PacketType)
        {
            case FlvAVCPacketType.SequenceHeader:
                ProcessAVCDecoderConfigurationRecord(body);
                return;
            case FlvAVCPacketType.Nalu:
                break;
            case FlvAVCPacketType.EndOfSequence:
                return;
            default:
                throw new InvalidDataException($"Invalid FlvAVCPacketType: {avcHeader.PacketType}");
        }

        // CompositionTime is SI24
        var compositionTime = (int)avcHeader.CompositionTime.HostValue;
        if ((compositionTime & 0x800000) != 0)
        {
            compositionTime -= 0x1000000;
        }

        // timestamp is in milliseconds; pts and dts are in 90kHz units
        long ptsMs = frameHeader.Timestamp + compositionTime;
        if (ptsMs < 0)
        {
            ptsMs = 0;
        }
        ulong dts = frameHeader.Timestamp * 90ul;
        ulong pts = (ulong)ptsMs * 90ul;

        BuildAccessUnit(body, frameHeader.IsKeyFrame);
        if (_esBuffer.WrittenCount == 0)
        {
            return;
        }

        if (frameHeader.IsKeyFrame)
        {
            _tsWriter.HasAudio = context.AudioCodec == AudioCodec.AAC;
            _tsWriter.WriteProgramTables(Outlet);
        }

        _tsWriter.WritePes(Outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            _esBuffer.WrittenSpan, pts, dts == pts ? null : dts, frameHeader.IsKeyFrame, withPcr: true);
        _esBuffer.Clear();
    }

    private void ProcessAVCDecoderConfigurationRecord(ReadOnlySequence<byte> buff)
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

    private void BuildAccessUnit(ReadOnlySequence<byte> buff, bool isKeyFrame)
    {
        Debug.Assert(_avcConfig != null);

        int lengthSize = _avcConfig!.Value.LengthSize;
        var remaining = (int)buff.Length;

        _esBuffer.Clear();
        _esBuffer.Write(s_accessUnitDelimiter);
        if (isKeyFrame && _parameterSets is not null)
        {
            _esBuffer.Write(_parameterSets);
        }

        while (remaining > 0)
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

            remaining -= lengthSize + length;
            if (remaining < 0)
            {
                throw new InvalidDataException("Invalid NALU length");
            }

            if (header.Type == NALUnitType.AUD)
            {
                // We prepend our own AUD; drop the source's one
                buff = buff.Slice(length);
                continue;
            }

            NALAnnexB.WriteNALUIndicator(_esBuffer);
            var writeBuff = _esBuffer.GetSpan(length);
            var readBuff = buff.Slice(0, length);
            readBuff.CopyTo(writeBuff);

            _esBuffer.Advance(length);
            buff = buff.Slice(readBuff.End);
        }
    }

    #endregion

    #region Audio (AAC)

    private const int AdtsHeaderSize = 7;

    private void ProcessAudioFrame(in MediaFrameHeader frameHeader, ReadOnlySequence<byte> payload)
    {
        var packetType = (FlvAACPacketType)payload.FirstSpan[0];
        var body = payload.Slice(1);

        switch (packetType)
        {
            case FlvAACPacketType.AACSequenceHeader:
                ProcessAudioSpecificConfig(body);
                return;
            case FlvAACPacketType.AACRaw:
                break;
            default:
                throw new InvalidDataException($"Invalid FlvAACPacketType: {packetType}");
        }

        if (_adtsTemplate is null)
        {
            s_logger.ZLogWarning($"AAC frame arrived before AudioSpecificConfig; dropped");
            return;
        }

        var aacLength = (int)body.Length;
        int frameLength = AdtsHeaderSize + aacLength;

        _esBuffer.Clear();
        var adts = _esBuffer.GetSpan(AdtsHeaderSize);
        _adtsTemplate.CopyTo(adts);
        adts[3] = (byte)(adts[3] | ((frameLength >> 11) & 0x03));
        adts[4] = (byte)(frameLength >> 3);
        adts[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        _esBuffer.Advance(AdtsHeaderSize);

        body.CopyTo(_esBuffer.GetSpan(aacLength));
        _esBuffer.Advance(aacLength);

        ulong pts = frameHeader.Timestamp * 90ul;
        _tsWriter.HasAudio = true;
        _tsWriter.WritePes(Outlet, M2TSWriter.PidAudio, M2TSWriter.StreamIdAudio,
            _esBuffer.WrittenSpan, pts, null, randomAccess: false, withPcr: false);
        _esBuffer.Clear();
    }

    private void ProcessAudioSpecificConfig(ReadOnlySequence<byte> buff)
    {
        Span<byte> asc = stackalloc byte[2];
        buff.Slice(0, 2).CopyTo(asc);

        int audioObjectType = asc[0] >> 3;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
        int channelConfiguration = (asc[1] >> 3) & 0x0F;

        s_logger.ZLogDebug(
            $"AudioSpecificConfig: AOT={audioObjectType}, SFI={samplingFrequencyIndex}, Channels={channelConfiguration}");

        // ADTS fixed header (no CRC); frame length fields are patched per frame
        var template = new byte[AdtsHeaderSize];
        template[0] = 0xFF;
        template[1] = 0xF1; // MPEG-4, layer 0, protection_absent
        template[2] = (byte)((((audioObjectType - 1) & 0x03) << 6)
                             | ((samplingFrequencyIndex & 0x0F) << 2)
                             | ((channelConfiguration >> 2) & 0x01));
        template[3] = (byte)((channelConfiguration & 0x03) << 6);
        template[4] = 0x00;
        template[5] = 0x00;
        template[6] = 0xFC; // buffer fullness low bits (0x7FF) + one AAC frame
        _adtsTemplate = template;
    }

    #endregion
}
