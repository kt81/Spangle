using System.Runtime.InteropServices;
using Spangle.Containers.M2TS;

namespace Spangle.Transport.HLS;

/// <summary>
/// Consumes an MPEG-2 TS packet stream, cuts it into segments at PAT boundaries
/// (the muxer writes PAT/PMT right before every keyframe), and maintains
/// a live M3U8 playlist in the output directory.
/// </summary>
internal sealed class HLSSegmenter
{
    private readonly IHLSStreamStorage _storage;
    private readonly double _targetDuration;
    private readonly HLSPlaylist _playlist;

    private readonly MemoryStream _current = new();

    // Packets held while deciding a cut (PAT+PMT for our own muxer's output).
    // Reused 188-byte buffers, so steady-state segmentation stays allocation-free.
    private readonly List<byte[]> _held = new();
    private int _heldCount;

    private bool  _pendingCut;
    private bool  _hasSegmentStart;
    private ulong _segmentStartPcr;
    private ulong _lastPcr;

    public HLSSegmenter(IHLSStreamStorage storage, double targetDuration, HLSPlaylistHandover? resume = null,
        Action<string, long, int>? onUpdated = null)
    {
        _storage = storage;
        _targetDuration = targetDuration;
        _playlist = new HLSPlaylist(storage, onUpdated: onUpdated, resume: resume);
    }

    /// <summary>
    /// Flushes the remaining data as a segment and exports the live playlist state
    /// for a successor session (takeover): no EXT-X-ENDLIST is written.
    /// </summary>
    public HLSPlaylistHandover ExportHandover()
    {
        FlushRemainder();
        return _playlist.ExportHandover();
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        ref readonly var header = ref MemoryMarshal.AsRef<TSHeader>(packet[..TSHeader.Size]);
        if (packet.Length != M2TSWriter.PacketSize || header.SyncByte != 0x47)
        {
            throw new InvalidDataException("Broken TS packet stream");
        }

        ushort pid = header.PID;
        bool payloadUnitStart = header.PayloadUnitStart != 0;
        ulong? pcr = TryReadPcr(packet);

        if (_pendingCut)
        {
            if (pcr.HasValue)
            {
                // The keyframe packet arrived; decide the cut with its own PCR
                double duration = (pcr.Value - _segmentStartPcr) / 90000.0;
                if (duration >= _targetDuration)
                {
                    FinalizeSegment(duration);
                    _segmentStartPcr = pcr.Value;
                }
                _lastPcr = pcr.Value;
                _pendingCut = false;
                WriteHeldPackets();
                _current.Write(packet);
            }
            else
            {
                // PAT/PMT possibly belonging to the next segment
                Hold(packet);
            }
            return;
        }

        if (pid == M2TSWriter.PidPat && payloadUnitStart && _hasSegmentStart)
        {
            _pendingCut = true;
            Hold(packet);
            return;
        }

        if (pcr.HasValue)
        {
            if (!_hasSegmentStart)
            {
                _hasSegmentStart = true;
                _segmentStartPcr = pcr.Value;
            }
            _lastPcr = pcr.Value;
        }

        _current.Write(packet);
    }

    /// <summary>
    /// Flushes the remaining data as the last segment and marks the playlist as ended.
    /// </summary>
    public void Complete()
    {
        FlushRemainder();
        _playlist.Complete();
    }

    private void Hold(ReadOnlySpan<byte> packet)
    {
        if (_heldCount == _held.Count)
        {
            _held.Add(new byte[M2TSWriter.PacketSize]);
        }
        packet.CopyTo(_held[_heldCount]);
        _heldCount++;
    }

    private void WriteHeldPackets()
    {
        for (var i = 0; i < _heldCount; i++)
        {
            _current.Write(_held[i]);
        }
        _heldCount = 0;
    }

    private void FlushRemainder()
    {
        WriteHeldPackets();

        if (_current.Length > 0 && _hasSegmentStart)
        {
            FinalizeSegment((_lastPcr - _segmentStartPcr) / 90000.0);
        }
    }

    private void FinalizeSegment(double duration)
    {
        string name = _playlist.NextSegmentName(".ts");
        // Write straight from the accumulation buffer; no per-segment copy
        _storage.WriteBlob(name, _current.GetBuffer().AsSpan(0, (int)_current.Length));
        _current.SetLength(0);
        _playlist.AddSegment(name, duration);
    }

    private static ulong? TryReadPcr(ReadOnlySpan<byte> packet)
    {
        ref readonly var ts = ref MemoryMarshal.AsRef<TSPacket>(packet);
        if (!ts.HasAdaptationFields || !ts.HasPCR
            // The flag alone is not proof on foreign streams; the field must be long enough
            || ts.AdaptationFields.AdaptationFieldLength < AdaptationFieldsBasic.Size - 1 + PCR.Size)
        {
            return null;
        }

        return ts.PCR.Base;
    }
}
