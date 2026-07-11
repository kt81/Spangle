using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spangle.Containers.M2TS;

/// <summary>
/// Writes MPEG-2 Transport Stream packets (PSI and PES) to an <see cref="IBufferWriter{T}"/>.
/// An instance keeps per-PID continuity counters, so it must be used for a single output stream
/// and must not be shared across threads.
/// </summary>
public sealed class M2TSWriter
{
    public const int PacketSize = 188;

    private const int PayloadMaxSize = PacketSize - TSHeader.Size;

    public const ushort PidPat   = 0x0000;
    public const ushort PidPmt   = 0x1000;
    public const ushort PidVideo = 0x0100;
    public const ushort PidAudio = 0x0101;

    public const byte StreamIdVideo = 0xE0;
    public const byte StreamIdAudio = 0xC0;

    private const byte StreamTypeH264    = M2TSStreamType.H264;
    private const byte StreamTypeH265    = M2TSStreamType.H265;
    private const byte StreamTypeAdtsAac = M2TSStreamType.AdtsAac;

    private const ulong PtsMask = (1ul << 33) - 1;

    private byte _ccPat;
    private byte _ccPmt;
    private byte _ccVideo;
    private byte _ccAudio;

    private bool _hasAudio;
    private bool _hasVideo = true;
    private byte _pmtVersion;
    private VideoCodec _videoCodec = Spangle.VideoCodec.H264;

    /// <summary>
    /// Removes the video stream from the PMT for audio-only programs; the PCR moves to
    /// the audio PID. The PMT version is incremented when this changes.
    /// </summary>
    public bool HasVideo
    {
        get => _hasVideo;
        set
        {
            if (_hasVideo == value)
            {
                return;
            }
            _hasVideo = value;
            _pmtVersion = (byte)((_pmtVersion + 1) & 0x1F);
        }
    }

    /// <summary>
    /// Adds the audio stream to the PMT. The PMT version is incremented when this changes.
    /// </summary>
    public bool HasAudio
    {
        get => _hasAudio;
        set
        {
            if (_hasAudio == value)
            {
                return;
            }
            _hasAudio = value;
            _pmtVersion = (byte)((_pmtVersion + 1) & 0x1F);
        }
    }

    /// <summary>
    /// The video codec declared in the PMT. The PMT version is incremented when this changes.
    /// </summary>
    public VideoCodec VideoCodec
    {
        get => _videoCodec;
        set
        {
            if (_videoCodec == value)
            {
                return;
            }
            _videoCodec = value;
            _pmtVersion = (byte)((_pmtVersion + 1) & 0x1F);
        }
    }

    private byte VideoStreamType => _videoCodec switch
    {
        Spangle.VideoCodec.H264 => StreamTypeH264,
        Spangle.VideoCodec.H265 => StreamTypeH265,
        _ => throw new NotSupportedException(
            $"{_videoCodec} has no MPEG-2 TS stream type; it requires the fMP4/CMAF pipeline"),
    };

    /// <summary>
    /// Writes a PAT packet followed by a PMT packet.
    /// Expected to be called at the head of each random access point so that
    /// a stream can be picked up from any segment boundary.
    /// </summary>
    public void WriteProgramTables(IBufferWriter<byte> outlet)
    {
        Span<byte> section = stackalloc byte[32];
        int len = BuildPat(section);
        WritePsiPacket(outlet, PidPat, section[..len], ref _ccPat);
        len = BuildPmt(section);
        WritePsiPacket(outlet, PidPmt, section[..len], ref _ccPmt);
    }

    /// <summary>
    /// Writes one PES packet (one access unit / frame) split into TS packets.
    /// </summary>
    /// <param name="outlet">Destination buffer</param>
    /// <param name="pid">PID of the elementary stream</param>
    /// <param name="streamId">PES stream_id (0xE0 video, 0xC0 audio)</param>
    /// <param name="payload">Elementary stream payload (e.g. Annex B access unit)</param>
    /// <param name="pts">Presentation timestamp in 90kHz</param>
    /// <param name="dts">Decoding timestamp in 90kHz; omit when equal to <paramref name="pts"/></param>
    /// <param name="randomAccess">Sets random_access_indicator on the first packet</param>
    /// <param name="withPcr">Writes PCR (= DTS or PTS) into the first packet</param>
    public void WritePes(IBufferWriter<byte> outlet, ushort pid, byte streamId,
        ReadOnlySpan<byte> payload, ulong? pts, ulong? dts, bool randomAccess, bool withPcr)
    {
        Debug.Assert(pts.HasValue || !dts.HasValue, "DTS without PTS is not allowed");

        Span<byte> pesHeader = stackalloc byte[19];
        int pesHeaderLen = BuildPesHeader(pesHeader, streamId, payload.Length, pts, dts);
        pesHeader = pesHeader[..pesHeaderLen];

        ulong? pcr = withPcr ? (dts ?? pts) : null;
        ref byte cc = ref GetContinuityCounter(pid);

        int total = pesHeaderLen + payload.Length;
        var offset = 0;
        var first = true;

        while (offset < total)
        {
            var pkt = outlet.GetSpan(PacketSize)[..PacketSize];
            int remaining = total - offset;

            var minAfLen = 0; // adaptation field length including its length byte
            if (first && (randomAccess || pcr.HasValue))
            {
                minAfLen = pcr.HasValue ? AdaptationFieldsBasic.Size + PCR.Size : AdaptationFieldsBasic.Size;
            }

            int afLen = minAfLen;
            int capacity = PayloadMaxSize - afLen;
            if (remaining < capacity)
            {
                // Grow the adaptation field with stuffing so that the payload fills the rest exactly
                afLen = PayloadMaxSize - remaining;
                capacity = remaining;
            }

            WriteTsHeader(pkt, pid, payloadUnitStart: first, hasAdaptationField: afLen > 0, cc);
            cc++;

            var pos = TSHeader.Size;
            if (afLen == 1)
            {
                pkt[pos++] = 0; // adaptation_field_length = 0: one stuffing byte in total
            }
            else if (afLen > 1)
            {
                bool carriesPcr = first && pcr.HasValue;
                pos += AdaptationFieldsBasic.ComposeTo(pkt[pos..],
                    adaptationFieldLength: (uint)(afLen - 1),
                    discontinuityIndicator: 0,
                    randomAccessIndicator: (byte)(first && randomAccess ? 1 : 0),
                    eSPriorityIndicator: 0,
                    hasPCR: carriesPcr,
                    hasOPCR: false,
                    hasSplicingPoint: false,
                    hasTransportPrivateData: false,
                    hasAdaptationFieldExtension: false);
                if (carriesPcr)
                {
                    WritePcrValue(pkt.Slice(pos, PCR.Size), pcr!.Value);
                    pos += PCR.Size;
                }
                int afEnd = TSHeader.Size + afLen;
                for (; pos < afEnd; pos++)
                {
                    pkt[pos] = 0xFF; // stuffing
                }
            }

            Debug.Assert(pos + capacity == PacketSize);
            CopyConcatenated(pesHeader, payload, offset, pkt.Slice(pos, capacity));
            offset += capacity;
            first = false;
            outlet.Advance(PacketSize);
        }
    }

    private ref byte GetContinuityCounter(ushort pid)
    {
        switch (pid)
        {
            case PidPat:
                return ref _ccPat;
            case PidPmt:
                return ref _ccPmt;
            case PidVideo:
                return ref _ccVideo;
            case PidAudio:
                return ref _ccAudio;
            default:
                throw new ArgumentOutOfRangeException(nameof(pid), pid, "Unknown PID");
        }
    }

    // Program association section: ISO/IEC 13818-1 Table 2-30
    private static int BuildPat(Span<byte> s)
    {
        s[0] = 0x00;                          // table_id: PAT
        s[1] = 0xB0;                          // section_syntax_indicator + reserved
        s[2] = 13;                            // section_length
        s[3] = 0x00; s[4] = 0x01;             // transport_stream_id = 1
        s[5] = 0xC1;                          // reserved + version 0 + current_next
        s[6] = 0x00;                          // section_number
        s[7] = 0x00;                          // last_section_number
        s[8] = 0x00; s[9] = 0x01;             // program_number = 1
        s[10] = 0xE0 | (PidPmt >> 8);         // reserved + PMT PID (high)
        s[11] = unchecked((byte)PidPmt);      // PMT PID (low)
        WriteCrc32(s, 12);
        return 16;
    }

    // TS program map section: ISO/IEC 13818-1 Table 2-33
    private int BuildPmt(Span<byte> s)
    {
        int streamCount = (_hasVideo ? 1 : 0) + (_hasAudio ? 1 : 0);
        int sectionLength = 9 + streamCount * 5 + 4;
        ushort pcrPid = _hasVideo ? PidVideo : PidAudio;
        s[0] = 0x02;                              // table_id: PMT
        s[1] = 0xB0;
        s[2] = (byte)sectionLength;
        s[3] = 0x00; s[4] = 0x01;                 // program_number = 1
        s[5] = (byte)(0xC1 | (_pmtVersion << 1)); // reserved + version + current_next
        s[6] = 0x00;
        s[7] = 0x00;
        s[8] = (byte)(0xE0 | (pcrPid >> 8));      // PCR PID: video, or audio when video is absent
        s[9] = unchecked((byte)pcrPid);
        s[10] = 0xF0; s[11] = 0x00;               // program_info_length = 0

        var pos = 12;
        if (_hasVideo)
        {
            s[pos++] = VideoStreamType;
            s[pos++] = 0xE0 | (PidVideo >> 8);
            s[pos++] = unchecked((byte)PidVideo);
            s[pos++] = 0xF0;
            s[pos++] = 0x00;                      // ES_info_length = 0
        }
        if (_hasAudio)
        {
            s[pos++] = StreamTypeAdtsAac;
            s[pos++] = 0xE0 | (PidAudio >> 8);
            s[pos++] = unchecked((byte)PidAudio);
            s[pos++] = 0xF0;
            s[pos++] = 0x00;
        }

        WriteCrc32(s, pos);
        return pos + 4;
    }

    private static void WriteCrc32(Span<byte> s, int sectionEnd)
    {
        uint crc = Crc32Mpeg2.Compute(s[..sectionEnd]);
        s[sectionEnd] = (byte)(crc >> 24);
        s[sectionEnd + 1] = (byte)(crc >> 16);
        s[sectionEnd + 2] = (byte)(crc >> 8);
        s[sectionEnd + 3] = (byte)crc;
    }

    private static void WritePsiPacket(IBufferWriter<byte> outlet, ushort pid, ReadOnlySpan<byte> section, ref byte cc)
    {
        var pkt = outlet.GetSpan(PacketSize)[..PacketSize];
        WriteTsHeader(pkt, pid, payloadUnitStart: true, hasAdaptationField: false, cc);
        cc++;
        pkt[TSHeader.Size] = 0x00; // pointer_field
        section.CopyTo(pkt[(TSHeader.Size + 1)..]);
        pkt[(TSHeader.Size + 1 + section.Length)..].Fill(0xFF);
        outlet.Advance(PacketSize);
    }

    private static void WriteTsHeader(Span<byte> pkt, ushort pid, bool payloadUnitStart, bool hasAdaptationField, byte cc)
    {
        TSHeader.ComposeTo(pkt,
            syncByte: 0x47,
            transportError: 0,
            payloadUnitStart: (byte)(payloadUnitStart ? 1 : 0),
            transportPriority: 0,
            pID: pid,
            transportScrambling: TSHeader.TransportScramblingType.None,
            adaptationFieldControl: hasAdaptationField
                ? TSHeader.AdaptationFieldControlType.AdaptationField | TSHeader.AdaptationFieldControlType.Payload
                : TSHeader.AdaptationFieldControlType.Payload,
            continuityCounter: cc); // masked to 4 bits by the generated composition
    }

    private static int BuildPesHeader(Span<byte> b, byte streamId, int payloadLength, ulong? pts, ulong? dts)
    {
        b[0] = 0x00;
        b[1] = 0x00;
        b[2] = 0x01;
        b[3] = streamId;

        int headerDataLength = (pts.HasValue ? 5 : 0) + (dts.HasValue ? 5 : 0);
        int pesPacketLength = 3 + headerDataLength + payloadLength;
        if (pesPacketLength > 0xFFFF)
        {
            pesPacketLength = 0; // unbounded; allowed for video in TS
        }
        b[4] = (byte)(pesPacketLength >> 8);
        b[5] = (byte)pesPacketLength;

        b[6] = 0x84; // marker '10' + data_alignment_indicator
        b[7] = (byte)((pts.HasValue ? 0x80 : 0) | (dts.HasValue ? 0x40 : 0));
        b[8] = (byte)headerDataLength;

        var pos = 9;
        if (pts.HasValue)
        {
            WriteTimestamp(b[pos..],
                dts.HasValue ? PESTimestamp.PrefixPtsOfPair : PESTimestamp.PrefixPtsOnly,
                pts.Value & PtsMask);
            pos += PESTimestamp.Size;
        }
        if (dts.HasValue)
        {
            WriteTimestamp(b[pos..], PESTimestamp.PrefixDts, dts.Value & PtsMask);
            pos += PESTimestamp.Size;
        }
        return pos;
    }

    private static void WriteTimestamp(Span<byte> b, byte prefix, ulong value)
    {
        PESTimestamp.ComposeTo(b, prefix, value); // marker bits included
    }

    private static void WritePcrValue(Span<byte> b, ulong pcr90kHz)
    {
        PCR.ComposeTo(b,
            @base: pcr90kHz & PtsMask,
            reserved: 0x3F, // all 1 on the wire
            extension: 0);
    }

    /// <summary>
    /// Copies <paramref name="dest"/>.Length bytes from the logical concatenation of
    /// <paramref name="a"/> and <paramref name="b"/> starting at <paramref name="offset"/>.
    /// </summary>
    private static void CopyConcatenated(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int offset, Span<byte> dest)
    {
        var copied = 0;
        if (offset < a.Length)
        {
            int n = Math.Min(a.Length - offset, dest.Length);
            a.Slice(offset, n).CopyTo(dest);
            copied = n;
        }
        if (copied < dest.Length)
        {
            b.Slice(offset + copied - a.Length, dest.Length - copied).CopyTo(dest[copied..]);
        }
    }
}

/// <summary>
/// CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final xor)
/// used for PSI section checksums.
/// </summary>
internal static class Crc32Mpeg2
{
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (byte t in data)
        {
            crc ^= (uint)t << 24;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }
        return crc;
    }
}
