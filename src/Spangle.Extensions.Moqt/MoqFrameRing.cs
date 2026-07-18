using System.Buffers;
using Spangle.Spinner;

namespace Spangle.Extensions.Moqt;

/// <summary>
/// The bounded buffer between the live pipeline and the relay. Its intake side always accepts,
/// so a slow or stalled relay can never push back through the fan-out onto the ingest — which
/// would stall the primary HLS output sharing that pipeline. When the byte budget runs out the
/// <em>oldest</em> frames are dropped: live viewers want the present, not a growing archive of
/// the gap. Video drops are reported to the consumer so it can abandon the now-broken group
/// and resume at the next keyframe, where a group may begin.
/// </summary>
internal sealed class MoqFrameRing : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<RingFrame> _frames = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly long _capacityBytes;
    private long _bufferedBytes;
    private int _droppedSinceDequeue;
    private bool _videoDroppedSinceDequeue;
    private bool _completed;

    public MoqFrameRing(long capacityBytes) => _capacityBytes = capacityBytes;

    /// <summary>
    /// Copies one frame in, dropping from the oldest end to stay within budget. Never blocks —
    /// that is the point of it.
    /// </summary>
    public void Enqueue(in MediaFrameHeader header, in ReadOnlySequence<byte> payload)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(header.Length);
        payload.CopyTo(buffer);

        lock (_lock)
        {
            if (_completed)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }

            _frames.Enqueue(new RingFrame(header, buffer));
            _bufferedBytes += header.Length;

            // Drop the oldest until within budget; the newest entry always survives, so one
            // frame larger than the whole budget still goes through alone.
            while (_bufferedBytes > _capacityBytes && _frames.Count > 1)
            {
                RingFrame dropped = _frames.Dequeue();
                _bufferedBytes -= dropped.Header.Length;
                _droppedSinceDequeue++;
                _videoDroppedSinceDequeue |= dropped.Header.Kind == MediaFrameKind.Video;
                dropped.Return();
            }
        }

        _available.Release();
    }

    /// <summary>No more frames are coming; a drained consumer gets a null frame.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
        }

        _available.Release();
    }

    /// <summary>
    /// Waits for the next frame. A null frame means the intake has ended. <c>Dropped</c> and
    /// <c>VideoDropped</c> report what fell off the ring since the previous dequeue — the
    /// consumer's cue to log, and to resume video at a keyframe.
    /// </summary>
    public async ValueTask<(RingFrame? Frame, int Dropped, bool VideoDropped)> DequeueAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (_frames.TryDequeue(out RingFrame frame))
                {
                    _bufferedBytes -= frame.Header.Length;
                    int dropped = _droppedSinceDequeue;
                    bool videoDropped = _videoDroppedSinceDequeue;
                    _droppedSinceDequeue = 0;
                    _videoDroppedSinceDequeue = false;
                    return (frame, dropped, videoDropped);
                }

                if (_completed)
                {
                    _available.Release(); // keep the completion wakeup for any later call
                    return (null, _droppedSinceDequeue, _videoDroppedSinceDequeue);
                }
            }

            // A dropped frame's leftover wakeup: nothing to take, wait again.
        }
    }

    /// <summary>Returns every remaining buffer to the pool; for the consumer's teardown.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            while (_frames.TryDequeue(out RingFrame frame))
            {
                frame.Return();
            }

            _bufferedBytes = 0;
        }
    }

    /// <summary>Dispose only after both sides have stopped; a waiting dequeue would throw.</summary>
    public void Dispose() => _available.Dispose();
}

/// <summary>One buffered frame: its header and a pooled copy of its payload.</summary>
internal readonly struct RingFrame
{
    private readonly byte[] _buffer;

    public RingFrame(in MediaFrameHeader header, byte[] buffer)
    {
        Header = header;
        _buffer = buffer;
    }

    public MediaFrameHeader Header { get; }

    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(0, Header.Length);

    /// <summary>Hands the pooled buffer back; the frame must not be touched afterwards.</summary>
    public void Return() => ArrayPool<byte>.Shared.Return(_buffer);
}
