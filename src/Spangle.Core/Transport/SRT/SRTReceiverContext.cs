using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Cysharp.Text;
using Spangle.Containers.M2TS;
using Spangle.Net.Transport.SRT;
using ZLogger;

namespace Spangle.Transport.SRT;

/// <summary>
/// Receives an MPEG-2 TS stream over SRT and emits canonical
/// <see cref="Spangle.Spinner.MediaFrameHeader"/> frames, exactly like the RTMP receiver:
/// the demuxer re-assembles PES payloads and the adapter normalizes H.264/AAC into
/// avcC/AudioSpecificConfig configs plus length-prefixed samples.
/// </summary>
public sealed class SRTReceiverContext : ReceiverContextBase<SRTReceiverContext>
{
    private readonly SRTClient _client;

    public override string Id { get; }
    public override bool IsCompleted => _client.IsCompleted;

    public override EndPoint EndPoint => _client.RemoteEndPoint;

    /// <summary>
    /// The stream name used for output routing, taken from the sender's SRT streamid
    /// (the SRT counterpart of an RTMP stream key). Null when the sender sent none.
    /// </summary>
    public override string? StreamName { get; }

    /// <summary>
    /// Forwards the aligned 188-byte packets to <see cref="IReceiverContext.MediaOutlet"/>
    /// as-is instead of demuxing to MediaFrames — for a TS-passthrough output path.
    /// The host must wire MediaOutlet before the session starts (no codec event fires
    /// in this mode) and pair it with a sender that consumes raw TS.
    /// </summary>
    public bool RawTsPassthrough { get; init; }

    public SRTReceiverContext(SRTClient client, CancellationToken ct)
        : base(client.Pipe.Input, client.Pipe.Output, ct)
    {
        _client = client;
        StreamName = ParseStreamName(client.StreamId);
        Id = StreamName ?? ZString.Format("SRT_{0}", client.PeerHandle);
    }

    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        // publish authorization (and same-name takeover) before any media is consumed
        if (PublishGate is { } gate && !await gate.TryOpenAsync(StreamName ?? Id, CancellationToken).ConfigureAwait(false))
        {
            Logger.ZLogInformation($"SRT publish rejected: {Id}");
            return;
        }

        bool raw = RawTsPassthrough && MediaOutlet is not null;
        M2TSDemuxer? demuxer = raw ? null : new M2TSDemuxer();
        M2TSMediaFrameAdapter<SRTReceiverContext>? adapter =
            raw ? null : new M2TSMediaFrameAdapter<SRTReceiverContext>(this);
        var packetCopy = new byte[M2TSWriter.PacketSize];
        var reader = RemoteReader;

        while (!IsCompleted)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var wrotePackets = false;
            ReadOnlySequence<byte> buff = result.Buffer;
            while (buff.Length >= M2TSWriter.PacketSize)
            {
                if (PeekByte(buff) != 0x47)
                {
                    // Lost 188-byte alignment: find a sync byte that is also followed
                    // by one a packet later, so we don't latch onto a 0x47 inside a
                    // payload. An unverifiable candidate waits for the next read.
                    if (!TryResync(ref buff, out bool needMoreData))
                    {
                        if (!needMoreData)
                        {
                            buff = buff.Slice(buff.End);
                        }
                        break;
                    }
                    Logger.ZLogWarning($"TS alignment lost; resynchronized");
                    continue;
                }

                ReadOnlySequence<byte> pktSeq = buff.Slice(0, M2TSWriter.PacketSize);
                if (raw)
                {
                    foreach (ReadOnlyMemory<byte> segment in pktSeq)
                    {
                        MediaOutlet!.Write(segment.Span);
                    }
                    wrotePackets = true;
                }
                else if (pktSeq.IsSingleSegment)
                {
                    demuxer!.ProcessPacket(pktSeq.FirstSpan, adapter!);
                }
                else
                {
                    pktSeq.CopyTo(packetCopy);
                    demuxer!.ProcessPacket(packetCopy, adapter!);
                }
                buff = buff.Slice(M2TSWriter.PacketSize);
            }

            AddBytesReceived(result.Buffer.Length - buff.Length);
            reader.AdvanceTo(buff.Start, result.Buffer.End);

            if ((wrotePackets || adapter is { HasPendingFrames: true }) && MediaOutlet is not null)
            {
                if (adapter is not null)
                {
                    adapter.HasPendingFrames = false;
                }
                await MediaOutlet.FlushAsync(CancellationToken).ConfigureAwait(false);
            }

            if (result.IsCompleted)
            {
                break;
            }
        }

        // Emit whatever was still being assembled. The flush must not observe the
        // (possibly already canceled) session token, or the tail frames are lost.
        demuxer?.Flush(adapter!);
        if (adapter is { HasPendingFrames: true } && MediaOutlet is not null)
        {
            adapter.HasPendingFrames = false;
            await MediaOutlet.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Logger.ZLogInformation($"SRT stream ended: {Id}");
    }

    private static byte PeekByte(in ReadOnlySequence<byte> buff)
    {
        ReadOnlySpan<byte> first = buff.FirstSpan;
        if (first.Length > 0)
        {
            return first[0];
        }
        Span<byte> tmp = stackalloc byte[1];
        buff.Slice(0, 1).CopyTo(tmp);
        return tmp[0];
    }

    /// <summary>
    /// Advances <paramref name="buff"/> to the next verified packet boundary: a 0x47
    /// with another 0x47 exactly one packet later. Returns false when no boundary was
    /// found; <paramref name="needMoreData"/> distinguishes "a candidate exists but
    /// there aren't enough bytes to verify it yet" (keep the tail for the next read).
    /// </summary>
    internal static bool TryResync(ref ReadOnlySequence<byte> buff, out bool needMoreData)
    {
        ReadOnlySequence<byte> search = buff.Slice(1);
        Span<byte> next = stackalloc byte[1]; // hoisted: stackalloc in a loop grows the stack per iteration
        while (search.PositionOf((byte)0x47) is { } sync)
        {
            ReadOnlySequence<byte> candidate = buff.Slice(sync);
            if (candidate.Length <= M2TSWriter.PacketSize)
            {
                buff = candidate;
                needMoreData = true;
                return false;
            }
            candidate.Slice(M2TSWriter.PacketSize, 1).CopyTo(next);
            if (next[0] == 0x47)
            {
                buff = candidate;
                needMoreData = false;
                return true;
            }
            search = candidate.Slice(1);
        }
        needMoreData = false;
        return false;
    }

    /// <summary>
    /// Extracts a routable stream name from an SRT streamid. Plain ids are used as-is;
    /// Haivision Access Control ids (#!::k=v,...) use their 'r' (resource) key.
    /// </summary>
    internal static string? ParseStreamName(string streamId)
    {
        if (string.IsNullOrEmpty(streamId))
        {
            return null;
        }
        if (!streamId.StartsWith("#!::", StringComparison.Ordinal))
        {
            return streamId;
        }

        foreach (Range part in streamId.AsSpan(4).Split(','))
        {
            ReadOnlySpan<char> kv = streamId.AsSpan(4)[part];
            if (kv.StartsWith("r=", StringComparison.Ordinal))
            {
                return kv.Length > 2 ? new string(kv[2..]) : null;
            }
        }
        return null;
    }
}
