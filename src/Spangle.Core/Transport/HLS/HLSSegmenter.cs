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
    private readonly string _directory;
    private readonly double _targetDuration;
    private readonly HLSPlaylist _playlist;

    private readonly MemoryStream _current = new();
    private readonly List<byte[]> _held = new();

    private bool  _pendingCut;
    private bool  _hasSegmentStart;
    private ulong _segmentStartPcr;
    private ulong _lastPcr;

    public HLSSegmenter(string directory, double targetDuration, HLSPlaylistHandover? resume = null)
    {
        _directory = directory;
        _targetDuration = targetDuration;
        Directory.CreateDirectory(directory);
        _playlist = new HLSPlaylist(directory, resume: resume);
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
                foreach (byte[] held in _held)
                {
                    _current.Write(held);
                }
                _held.Clear();
                _current.Write(packet);
            }
            else
            {
                // PAT/PMT possibly belonging to the next segment
                _held.Add(packet.ToArray());
            }
            return;
        }

        if (pid == M2TSWriter.PidPat && payloadUnitStart && _hasSegmentStart)
        {
            _pendingCut = true;
            _held.Add(packet.ToArray());
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

    private void FlushRemainder()
    {
        foreach (byte[] held in _held)
        {
            _current.Write(held);
        }
        _held.Clear();

        if (_current.Length > 0 && _hasSegmentStart)
        {
            FinalizeSegment((_lastPcr - _segmentStartPcr) / 90000.0);
        }
    }

    private void FinalizeSegment(double duration)
    {
        string name = _playlist.NextSegmentName(".ts");
        using (var file = File.Create(Path.Combine(_directory, name)))
        {
            // Write straight from the accumulation buffer; no per-segment copy
            file.Write(_current.GetBuffer(), 0, (int)_current.Length);
        }
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
