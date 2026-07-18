using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Spangle.Codecs;
using Spangle.Codecs.AVC;
using Spangle.Codecs.HEVC;
using Spangle.Containers.M2TS;
using Spangle.Interop;
using Spangle.Logging;
using Spangle.Transport.Rtmp;
using ZLogger;

namespace Spangle.Spinner;

/// <summary>
/// Converts <see cref="MediaFrameHeader"/>-framed media (H.264/H.265 video and AAC audio)
/// into an MPEG-2 TS stream (PAT/PMT + PES with Annex B / ADTS payload).
/// </summary>
public sealed class FlvToM2TSSpinner(IReceiverContext context, PipeWriter anotherIntake, CancellationToken ct)
    : SpinnerBase<FlvToM2TSSpinner>(anotherIntake, ct)
{
    // Access unit delimiter NALUs, prepended to every access unit
    private static readonly byte[] s_audH264 = [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];
    private static readonly byte[] s_audH265 = [0x00, 0x00, 0x00, 0x01, 0x46, 0x01, 0x50];

    // ---- Video track state (established by the codec config frame) ----
    private VideoCodec? _videoCodec;
    private int         _naluLengthSize;

    /// <summary>Parameter sets (VPS/SPS/PPS) in Annex B form, re-injected at every keyframe for segment independence.</summary>
    private byte[]? _parameterSets;

    // ---- Audio track state ----
    /// <summary>ADTS header template built from the AudioSpecificConfig; bytes 3..5 are patched per frame.</summary>
    private byte[]? _adtsTemplate;

    /// <summary>The ASC declared something ADTS cannot carry; audio frames are dropped silently.</summary>
    private bool _audioUnrepresentable;

    // ---- Audio-only mode (the source declared no video track) ----
    /// <summary>90kHz PTS of the last PAT/PMT emission; tables repeat about once a second.</summary>
    private ulong _lastTablesPts;
    private bool  _tablesWritten;
    private const ulong TablesInterval90k = 90_000; // 1 second

    private readonly ArrayBufferWriter<byte> _esBuffer = new(4096);
    private readonly M2TSWriter _tsWriter = new();

    private static readonly ILogger<FlvToM2TSSpinner> s_logger = SpangleLogManager.GetLogger<FlvToM2TSSpinner>();

    public override async ValueTask SpinAsync()
    {
        try
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                var result = await IntakeReader.ReadAtLeastAsync(MediaFrameHeader.Size, CancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length < MediaFrameHeader.Size)
                {
                    break; // intake completed
                }
                var headerBuff = result.Buffer.Slice(0, MediaFrameHeader.Size);
                var header = MediaFrameHeader.Read(headerBuff);
                IntakeReader.AdvanceTo(headerBuff.End);

                if (header.Length <= 0)
                {
                    continue;
                }

                result = await IntakeReader.ReadAtLeastAsync(header.Length, CancellationToken).ConfigureAwait(false);
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
                    case MediaFrameKind.Data:
                        ProcessDataFrame(in header, payload);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown media frame kind: {header.Kind}");
                }

                IntakeReader.AdvanceTo(payload.End);
                await Outlet.FlushAsync(CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await IntakeReader.CompleteAsync().ConfigureAwait(false);
            await Outlet.CompleteAsync().ConfigureAwait(false);
        }
    }

    #region Video

    private void ProcessVideoFrame(in MediaFrameHeader frameHeader, ReadOnlySequence<byte> payload)
    {
        if (frameHeader.IsConfig)
        {
            ProcessVideoConfig(frameHeader.VideoCodec, payload);
            return;
        }

        if (_videoCodec is null)
        {
            s_logger.ZLogWarning($"Video frame arrived before the codec config; dropped");
            return;
        }

        // timestamps are already 90 kHz ticks — the PES clock's own unit, so nothing scales here.
        // A negative composition offset would put PTS before DTS, which no decoder accepts; clamp
        // PTS up to DTS so the PES stays valid.
        ulong dts = (ulong)frameHeader.Timestamp;
        long ptsTicks = frameHeader.Timestamp + frameHeader.CompositionTime;
        ulong pts = ptsTicks > frameHeader.Timestamp ? (ulong)ptsTicks : dts;

        BuildAccessUnit(payload, frameHeader.IsKeyFrame);
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
        _esBuffer.ResetWrittenCount();
    }

    private void ProcessVideoConfig(VideoCodec codec, ReadOnlySequence<byte> payload)
    {
        var parameterSets = new ArrayBufferWriter<byte>(256);
        _naluLengthSize = codec switch
        {
            VideoCodec.H264 => AVCDecoderConfigurationRecordReader.Parse(payload, parameterSets),
            VideoCodec.H265 => HEVCDecoderConfigurationRecord.Parse(payload, parameterSets),
            _ => throw new NotInScopeException(
                $"{codec} cannot be carried in MPEG-2 TS; it requires the fMP4/CMAF pipeline (not implemented yet)"),
        };
        _parameterSets = parameterSets.WrittenSpan.ToArray();
        _videoCodec = codec;
        _tsWriter.VideoCodec = codec;
        s_logger.ZLogDebug($"Video config: codec={codec}, naluLengthSize={_naluLengthSize}, parameterSets={_parameterSets.Length} bytes");
    }

    /// <summary>
    /// Rebuilds one access unit in Annex B form:
    /// AUD, then (for keyframes) the parameter sets, then the frame's NALUs
    /// converted from length-prefixed to start-code form.
    /// </summary>
    private void BuildAccessUnit(ReadOnlySequence<byte> buff, bool isKeyFrame)
    {
        _esBuffer.ResetWrittenCount();
        _esBuffer.Write(_videoCodec == VideoCodec.H265 ? s_audH265 : s_audH264);
        if (isKeyFrame && _parameterSets is not null)
        {
            _esBuffer.Write(_parameterSets);
        }

        var remaining = (int)buff.Length;
        while (remaining > 0)
        {
            int length = ReadNaluLength(ref buff);

            remaining -= _naluLengthSize + length;
            if (remaining < 0)
            {
                throw new InvalidDataException("Invalid NALU length");
            }

            if (IsAudNalu(PeekByte(buff)))
            {
                // We prepend our own AUD; drop the source's one
                buff = buff.Slice(length);
                continue;
            }

            NALAnnexB.WriteNALUIndicator(_esBuffer);
            var naluBuff = buff.Slice(0, length);
            naluBuff.CopyTo(_esBuffer.GetSpan(length));
            _esBuffer.Advance(length);
            buff = buff.Slice(naluBuff.End);
        }
    }

    private int ReadNaluLength(ref ReadOnlySequence<byte> buff)
    {
        var lengthBuff = buff.Slice(0, _naluLengthSize);
        int length;
        switch (_naluLengthSize)
        {
            case 1:
                length = PeekByte(lengthBuff);
                break;
            case 2:
                length = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(lengthBuff).HostValue;
                break;
            case 4:
                length = (int)BufferMarshal.AsRefOrCopy<BigEndianUInt32>(lengthBuff).HostValue;
                break;
            default:
                // Not to be reached
                throw new InvalidDataException();
        }

        buff = buff.Slice(lengthBuff.End);
        return length;
    }

    private bool IsAudNalu(byte naluFirstByte) => _videoCodec == VideoCodec.H265
        ? ((naluFirstByte >> 1) & 0x3F) == 35 // HEVC AUD_NUT
        : (naluFirstByte & 0x1F) == (byte)NALUnitType.AUD;

    private static byte PeekByte(in ReadOnlySequence<byte> buff)
    {
        var span = buff.FirstSpan;
        if (span.Length > 0)
        {
            return span[0];
        }
        Span<byte> one = stackalloc byte[1];
        buff.Slice(0, 1).CopyTo(one);
        return one[0];
    }

    #endregion

    #region Timed metadata (ID3)

    private void ProcessDataFrame(in MediaFrameHeader frameHeader, ReadOnlySequence<byte> payload)
    {
        if ((DataCodec)frameHeader.Codec != DataCodec.Id3)
        {
            // e.g. raw AMF0 events when no converting spinner is in the chain
            s_logger.ZLogTrace($"Dropping non-ID3 data frame: {(DataCodec)frameHeader.Codec}");
            return;
        }

        // Announced in the PMT from the next program table emission on
        _tsWriter.HasTimedId3 = true;

        _esBuffer.ResetWrittenCount();
        payload.CopyTo(_esBuffer.GetSpan((int)payload.Length));
        _esBuffer.Advance((int)payload.Length);

        ulong pts = (ulong)frameHeader.Timestamp;
        _tsWriter.WritePes(Outlet, M2TSWriter.PidData, M2TSWriter.StreamIdPrivate1,
            _esBuffer.WrittenSpan, pts, null, randomAccess: false, withPcr: false);
        _esBuffer.ResetWrittenCount();
    }

    #endregion

    #region Audio (AAC)

    private const int AdtsHeaderSize = 7;

    private void ProcessAudioFrame(in MediaFrameHeader frameHeader, ReadOnlySequence<byte> payload)
    {
        if (frameHeader.AudioCodec != AudioCodec.AAC)
        {
            if (!_audioUnrepresentable)
            {
                _audioUnrepresentable = true;
                s_logger.ZLogWarning(
                    $"{frameHeader.AudioCodec} cannot be carried in this TS output; audio is dropped (use the fMP4/CMAF segment format)");
            }
            return;
        }

        if (frameHeader.IsConfig)
        {
            ProcessAudioSpecificConfig(payload);
            return;
        }

        if (_adtsTemplate is null)
        {
            if (!_audioUnrepresentable)
            {
                s_logger.ZLogWarning($"AAC frame arrived before AudioSpecificConfig; dropped");
            }
            return;
        }

        var aacLength = (int)payload.Length;
        int frameLength = AdtsHeaderSize + aacLength;
        if (frameLength > 0x1FFF)
        {
            // the ADTS frame length field is 13 bits; anything larger would silently
            // truncate and desynchronize the stream
            s_logger.ZLogWarning($"AAC frame too large for ADTS ({aacLength} bytes); dropped");
            return;
        }

        _esBuffer.ResetWrittenCount();
        var adts = _esBuffer.GetSpan(AdtsHeaderSize);
        _adtsTemplate.CopyTo(adts);
        adts[3] = (byte)(adts[3] | ((frameLength >> 11) & 0x03));
        adts[4] = (byte)(frameLength >> 3);
        adts[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        _esBuffer.Advance(AdtsHeaderSize);

        payload.CopyTo(_esBuffer.GetSpan(aacLength));
        _esBuffer.Advance(aacLength);

        ulong pts = (ulong)frameHeader.Timestamp;
        _tsWriter.HasAudio = true;

        bool audioOnly = context.IsAudioOnly;
        if (audioOnly)
        {
            // No video keyframes exist to carry the program tables and the PCR, so the
            // audio track does: tables about once a second (the segmenter cuts at PAT
            // boundaries) and a PCR + random_access_indicator on every audio PES
            // (every AAC frame is a sync point).
            _tsWriter.HasVideo = false;
            if (!_tablesWritten || pts - _lastTablesPts >= TablesInterval90k)
            {
                _tsWriter.WriteProgramTables(Outlet);
                _lastTablesPts = pts;
                _tablesWritten = true;
            }
        }

        _tsWriter.WritePes(Outlet, M2TSWriter.PidAudio, M2TSWriter.StreamIdAudio,
            _esBuffer.WrittenSpan, pts, null, randomAccess: audioOnly, withPcr: audioOnly);
        _esBuffer.ResetWrittenCount();
    }

    private void ProcessAudioSpecificConfig(ReadOnlySequence<byte> buff)
    {
        Span<byte> asc = stackalloc byte[2];
        buff.Slice(0, 2).CopyTo(asc);

        int audioObjectType = asc[0] >> 3;
        int samplingFrequencyIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
        int channelConfiguration = (asc[1] >> 3) & 0x0F;

        // ADTS cannot express every AudioSpecificConfig: the profile field is 2 bits
        // (AOT 1-4) and the frequency must be an index (SFI 15 = explicit 24-bit rate).
        // HE-AAC (SBR/PS) is representable as its AAC-LC core with implicit signaling.
        if (audioObjectType is 5 or 29)
        {
            s_logger.ZLogInformation($"HE-AAC signaled as its AAC-LC core in ADTS (SBR is implicit for decoders)");
            audioObjectType = 2; // the base SFI in the ASC is the core AAC sample rate
        }
        if (audioObjectType is < 1 or > 4 || samplingFrequencyIndex == 15)
        {
            s_logger.ZLogWarning(
                $"AudioSpecificConfig not representable in ADTS (AOT={audioObjectType}, SFI={samplingFrequencyIndex}); audio is dropped");
            _audioUnrepresentable = true;
            _adtsTemplate = null;
            return;
        }

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
