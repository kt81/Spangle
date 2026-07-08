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

    public HLSSegmenter(string directory, double targetDuration)
    {
        _directory = directory;
        _targetDuration = targetDuration;
        Directory.CreateDirectory(directory);
        _playlist = new HLSPlaylist(directory);
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != M2TSWriter.PacketSize || packet[0] != 0x47)
        {
            throw new InvalidDataException("Broken TS packet stream");
        }

        var pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
        bool payloadUnitStart = (packet[1] & 0x40) != 0;
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
        foreach (byte[] held in _held)
        {
            _current.Write(held);
        }
        _held.Clear();

        if (_current.Length > 0 && _hasSegmentStart)
        {
            FinalizeSegment((_lastPcr - _segmentStartPcr) / 90000.0);
        }
        _playlist.Complete();
    }

    private void FinalizeSegment(double duration)
    {
        string name = _playlist.NextSegmentName(".ts");
        File.WriteAllBytes(Path.Combine(_directory, name), _current.ToArray());
        _current.SetLength(0);
        _playlist.AddSegment(name, duration);
    }

    private static ulong? TryReadPcr(ReadOnlySpan<byte> packet)
    {
        bool hasAdaptationField = (packet[3] & 0x20) != 0;
        if (!hasAdaptationField || packet[4] < 7)
        {
            return null;
        }
        bool hasPcr = (packet[5] & 0x10) != 0;
        if (!hasPcr)
        {
            return null;
        }

        return ((ulong)packet[6] << 25)
               | ((ulong)packet[7] << 17)
               | ((ulong)packet[8] << 9)
               | ((ulong)packet[9] << 1)
               | ((ulong)packet[10] >> 7);
    }
}
