using System.Buffers;

namespace Spangle.Transport.Rtsp.Rtp;

/// <summary>
/// A small bounded resequencing buffer for RTP over UDP, where datagrams can arrive out
/// of order. It releases packets in ascending sequence-number order (wrap-aware); a gap
/// is tolerated until the window fills, then the buffer advances past the missing packet
/// so a real loss is reported to the depacketizer rather than stalling the stream.
/// TCP-interleaved transport is already ordered and bypasses this entirely.
/// </summary>
internal sealed class RtpReorderBuffer(int windowSize, Action<byte[], int> emit)
{
    // seq -> (pooled buffer, length). Small window, so a dictionary keyed by the 16-bit
    // sequence number is fine; ordering is resolved by signed distance from _next.
    private readonly Dictionary<ushort, (byte[] Buffer, int Length)> _pending = new();
    private ushort _next;
    private bool _started;

    /// <summary>Adds one received RTP datagram; emits whatever is now releasable in order.</summary>
    public void Add(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 4)
        {
            return; // too short to carry an RTP sequence number
        }
        var seq = (ushort)((datagram[2] << 8) | datagram[3]);
        if (!_started)
        {
            _started = true;
            _next = seq;
        }
        else if ((short)(seq - _next) < 0)
        {
            return; // already released past this one, or a duplicate — drop it
        }

        if (!_pending.ContainsKey(seq))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
            datagram.CopyTo(buffer);
            _pending[seq] = (buffer, datagram.Length);
        }

        Drain();
        if (_pending.Count > windowSize)
        {
            AdvancePastGap();
            Drain();
        }
    }

    /// <summary>Releases everything still buffered, in order — call on shutdown.</summary>
    public void Flush()
    {
        while (_pending.Count > 0)
        {
            AdvancePastGap();
            Drain();
        }
    }

    private void Drain()
    {
        while (_pending.Remove(_next, out (byte[] Buffer, int Length) entry))
        {
            emit(entry.Buffer, entry.Length);
            ArrayPool<byte>.Shared.Return(entry.Buffer);
            _next++;
        }
    }

    /// <summary>The packet at <see cref="_next"/> is missing; jump to the oldest buffered.</summary>
    private void AdvancePastGap()
    {
        ushort? oldest = null;
        var oldestDistance = int.MaxValue;
        foreach (ushort seq in _pending.Keys)
        {
            int distance = (ushort)(seq - _next);
            if (distance < oldestDistance)
            {
                oldestDistance = distance;
                oldest = seq;
            }
        }
        if (oldest is { } target)
        {
            _next = target;
        }
    }
}
