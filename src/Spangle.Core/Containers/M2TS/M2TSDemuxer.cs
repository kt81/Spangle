using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Containers.M2TS;

/// <summary>
/// Receives demultiplexed elementary-stream data from <see cref="M2TSDemuxer"/>.
/// </summary>
internal interface IM2TSDemuxerSink
{
    /// <summary>
    /// Called when the PMT is parsed for the first time or changes.
    /// Stream types are raw ISO 13818-1 values (0 = the track is absent).
    /// </summary>
    void OnProgramMapped(byte videoStreamType, byte audioStreamType);

    /// <summary>One complete PES payload (one video access unit, or one or more ADTS frames).</summary>
    void OnPes(byte streamType, ulong? pts90k, ulong? dts90k, ReadOnlySpan<byte> es);
}

/// <summary>
/// MPEG-2 TS demultiplexer: the mirror of <see cref="M2TSWriter"/>.
/// Feeds on 188-byte packets and emits PAT/PMT knowledge and reassembled PES
/// payloads to a sink. Container-level only: it does not know codecs.
/// </summary>
internal sealed class M2TSDemuxer
{
    private static readonly ILogger<M2TSDemuxer> s_logger = SpangleLogManager.GetLogger<M2TSDemuxer>();

    private ushort _pmtPid;
    private int    _pmtVersion = -1;

    private sealed class TrackState
    {
        public byte  StreamType;
        public byte  LastContinuity = 0xFF;
        public bool  Assembling;
        public bool  Corrupt;
        public ulong? Pts;
        public ulong? Dts;
        public readonly ArrayBufferWriter<byte> Assembly = new(16 * 1024);
    }

    private readonly Dictionary<ushort, TrackState> _tracks = new();

    /// <summary>Processes one 188-byte TS packet.</summary>
    public void ProcessPacket(ReadOnlySpan<byte> packet, IM2TSDemuxerSink sink)
    {
        ref readonly var header = ref MemoryMarshal.AsRef<TSHeader>(packet[..TSHeader.Size]);
        if (header.SyncByte != 0x47)
        {
            s_logger.ZLogWarning($"Lost TS sync (0x{header.SyncByte:X2}); packet dropped");
            return;
        }
        if (header.TransportError != 0)
        {
            return;
        }

        var afc = header.AdaptationFieldControl;
        bool hasPayload = (afc & TSHeader.AdaptationFieldControlType.Payload) != 0;
        if (!hasPayload)
        {
            return;
        }

        int payloadStart = TSHeader.Size;
        if ((afc & TSHeader.AdaptationFieldControlType.AdaptationField) != 0)
        {
            payloadStart += 1 + packet[TSHeader.Size]; // adaptation_field_length byte + body
        }
        if (payloadStart >= packet.Length)
        {
            return;
        }

        ReadOnlySpan<byte> payload = packet[payloadStart..];
        ushort pid = header.PID;
        bool pusi = header.PayloadUnitStart != 0;

        if (pid == 0x0000)
        {
            ProcessPsiPacket(_patSection, payload, pusi, header.ContinuityCounter, PatTableId, sink);
            return;
        }
        if (_pmtPid != 0 && pid == _pmtPid)
        {
            ProcessPsiPacket(_pmtSection, payload, pusi, header.ContinuityCounter, PmtTableId, sink);
            return;
        }

        if (_tracks.TryGetValue(pid, out TrackState? track))
        {
            ProcessEsPacket(track, payload, pusi, header.ContinuityCounter, sink);
        }
    }

    /// <summary>Emits any pending PES assemblies (end of stream).</summary>
    public void Flush(IM2TSDemuxerSink sink)
    {
        foreach (TrackState track in _tracks.Values)
        {
            EmitAssembled(track, sink);
        }
    }

    // =======================================================================
    // PSI — a section may span TS packets (section_length goes up to 1021 bytes,
    // a packet payload holds 184), so each PSI PID gets a small reassembly buffer,
    // mirroring the PES track assembly.

    private const byte PatTableId = 0x00;
    private const byte PmtTableId = 0x02;

    private sealed class SectionState
    {
        public byte LastContinuity = 0xFF;
        public bool Assembling;
        public readonly ArrayBufferWriter<byte> Assembly = new(1024);

        public void Reset()
        {
            Assembling = false;
            Assembly.ResetWrittenCount();
        }
    }

    private readonly SectionState _patSection = new();
    private readonly SectionState _pmtSection = new();

    private void ProcessPsiPacket(SectionState state, ReadOnlySpan<byte> payload, bool pusi, byte continuity,
        byte tableId, IM2TSDemuxerSink sink)
    {
        if (state.LastContinuity != 0xFF)
        {
            if (continuity == state.LastContinuity)
            {
                return; // duplicate packet
            }
            var expected = (byte)((state.LastContinuity + 1) & 0x0F);
            if (continuity != expected && state.Assembling)
            {
                s_logger.ZLogWarning($"TS continuity broken on a PSI PID; dropping the partial section");
                state.Reset();
            }
        }
        state.LastContinuity = continuity;

        if (pusi)
        {
            if (payload.Length < 1)
            {
                return;
            }
            int pointer = payload[0];
            if (1 + pointer > payload.Length)
            {
                state.Reset();
                return;
            }
            // the bytes before the pointer are the tail of the section under assembly
            if (state.Assembling && pointer > 0)
            {
                AppendSection(state, payload.Slice(1, pointer));
                TryCompleteSection(state, tableId, sink);
            }
            state.Reset();
            ConsumeSections(state, payload[(1 + pointer)..], tableId, sink);
        }
        else if (state.Assembling)
        {
            AppendSection(state, payload);
            TryCompleteSection(state, tableId, sink);
        }
    }

    /// <summary>
    /// Parses as many complete sections as <paramref name="data"/> holds; an incomplete
    /// trailing section is buffered until its continuation packets arrive.
    /// </summary>
    private void ConsumeSections(SectionState state, ReadOnlySpan<byte> data, byte tableId, IM2TSDemuxerSink sink)
    {
        while (data.Length > 0 && data[0] != 0xFF /* stuffing */)
        {
            if (data.Length >= 3)
            {
                int total = 3 + (((data[1] & 0x0F) << 8) | data[2]);
                if (total <= data.Length)
                {
                    DispatchSection(data[..total], tableId, sink);
                    data = data[total..];
                    continue;
                }
            }
            state.Assembling = true;
            AppendSection(state, data);
            return;
        }
    }

    private static void AppendSection(SectionState state, ReadOnlySpan<byte> data)
    {
        data.CopyTo(state.Assembly.GetSpan(data.Length));
        state.Assembly.Advance(data.Length);
    }

    private void TryCompleteSection(SectionState state, byte tableId, IM2TSDemuxerSink sink)
    {
        ReadOnlySpan<byte> buff = state.Assembly.WrittenSpan;
        if (buff.Length < 3)
        {
            return;
        }
        int total = 3 + (((buff[1] & 0x0F) << 8) | buff[2]);
        if (buff.Length < total)
        {
            return; // still waiting for continuation packets
        }
        DispatchSection(buff[..total], tableId, sink);
        state.Reset();
    }

    private void DispatchSection(ReadOnlySpan<byte> section, byte tableId, IM2TSDemuxerSink sink)
    {
        if (section[0] != tableId)
        {
            return;
        }
        if (tableId == PatTableId)
        {
            ParsePat(section);
        }
        else
        {
            ParsePmt(section, sink);
        }
    }

    private void ParsePat(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12)
        {
            return;
        }

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        // program loop: after the 5 fixed bytes, entries of 4 bytes until the CRC
        int end = Math.Min(3 + sectionLength - 4, section.Length);
        for (int i = 8; i + 4 <= end; i += 4)
        {
            int programNumber = (section[i] << 8) | section[i + 1];
            if (programNumber == 0)
            {
                continue; // network PID
            }
            _pmtPid = (ushort)(((section[i + 2] & 0x1F) << 8) | section[i + 3]);
            return; // single program stream
        }
    }

    private void ParsePmt(ReadOnlySpan<byte> section, IM2TSDemuxerSink sink)
    {
        if (section.Length < 16)
        {
            return;
        }

        int version = (section[5] >> 1) & 0x1F;
        if (version == _pmtVersion)
        {
            return; // unchanged
        }

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int pos = 12 + programInfoLength;
        int end = Math.Min(3 + sectionLength - 4, section.Length); // stop before the CRC

        byte videoStreamType = 0;
        byte audioStreamType = 0;
        var newTracks = new Dictionary<ushort, TrackState>();
        while (pos + 5 <= end)
        {
            byte streamType = section[pos];
            var esPid = (ushort)(((section[pos + 1] & 0x1F) << 8) | section[pos + 2]);
            int esInfoLength = ((section[pos + 3] & 0x0F) << 8) | section[pos + 4];
            pos += 5 + esInfoLength;

            switch (streamType)
            {
                case M2TSStreamType.H264 when videoStreamType == 0:
                case M2TSStreamType.H265 when videoStreamType == 0:
                    videoStreamType = streamType;
                    newTracks[esPid] = _tracks.TryGetValue(esPid, out var vt) ? vt : new TrackState();
                    newTracks[esPid].StreamType = streamType;
                    break;
                case M2TSStreamType.AdtsAac when audioStreamType == 0:
                    audioStreamType = streamType;
                    newTracks[esPid] = _tracks.TryGetValue(esPid, out var at) ? at : new TrackState();
                    newTracks[esPid].StreamType = streamType;
                    break;
                default:
                    s_logger.ZLogInformation($"Ignoring PMT stream_type 0x{streamType:X2} (PID {esPid})");
                    break;
            }
        }

        _pmtVersion = version;
        _tracks.Clear();
        foreach ((ushort esPid, TrackState track) in newTracks)
        {
            _tracks[esPid] = track;
        }

        s_logger.ZLogDebug($"PMT mapped: video=0x{videoStreamType:X2} audio=0x{audioStreamType:X2}");
        sink.OnProgramMapped(videoStreamType, audioStreamType);
    }

    // =======================================================================
    // PES

    private void ProcessEsPacket(TrackState track, ReadOnlySpan<byte> payload, bool pusi, byte continuity,
        IM2TSDemuxerSink sink)
    {
        // continuity check: a lost packet invalidates the frame under assembly
        if (track.LastContinuity != 0xFF && !pusi)
        {
            var expected = (byte)((track.LastContinuity + 1) & 0x0F);
            if (continuity != expected && continuity != track.LastContinuity /* duplicate */)
            {
                if (!track.Corrupt)
                {
                    s_logger.ZLogWarning($"TS continuity broken (expected {expected}, got {continuity}); dropping the current frame");
                }
                track.Corrupt = true;
            }
            else if (continuity == track.LastContinuity)
            {
                return; // duplicate packet
            }
        }
        track.LastContinuity = continuity;

        if (pusi)
        {
            EmitAssembled(track, sink);
            track.Corrupt = false;

            // PES header: 00 00 01 stream_id len(2) '10'+flags(2) header_length(1) [PTS[ DTS]]
            if (payload.Length < 9 || payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
            {
                s_logger.ZLogWarning($"Broken PES start; dropping until the next unit");
                track.Corrupt = true;
                return;
            }

            byte ptsDtsFlags = (byte)(payload[7] >> 6);
            int headerDataLength = payload[8];
            int esStart = 9 + headerDataLength;
            if (esStart > payload.Length)
            {
                s_logger.ZLogWarning($"PES header exceeds the packet payload; dropping until the next unit");
                track.Corrupt = true;
                return;
            }

            track.Pts = null;
            track.Dts = null;
            if ((ptsDtsFlags & 0b10) != 0 && headerDataLength >= 5)
            {
                ref readonly var pts = ref MemoryMarshal.AsRef<PESTimestamp>(payload.Slice(9, PESTimestamp.Size));
                track.Pts = pts.Value;
                if (ptsDtsFlags == 0b11 && headerDataLength >= 10)
                {
                    ref readonly var dts = ref MemoryMarshal.AsRef<PESTimestamp>(payload.Slice(14, PESTimestamp.Size));
                    track.Dts = dts.Value;
                }
            }

            track.Assembling = true;
            Append(track, payload[esStart..]);
        }
        else if (track.Assembling && !track.Corrupt)
        {
            Append(track, payload);
        }
    }

    private static void Append(TrackState track, ReadOnlySpan<byte> es)
    {
        if (es.Length == 0)
        {
            return;
        }
        es.CopyTo(track.Assembly.GetSpan(es.Length));
        track.Assembly.Advance(es.Length);
    }

    private static void EmitAssembled(TrackState track, IM2TSDemuxerSink sink)
    {
        if (track.Assembling && !track.Corrupt && track.Assembly.WrittenCount > 0)
        {
            sink.OnPes(track.StreamType, track.Pts, track.Dts, track.Assembly.WrittenSpan);
        }
        track.Assembly.ResetWrittenCount();
        track.Assembling = false;
    }
}
