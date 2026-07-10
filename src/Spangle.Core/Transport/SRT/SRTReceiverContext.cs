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

    public SRTReceiverContext(SRTClient client, CancellationToken ct)
        : base(client.Pipe.Input, client.Pipe.Output, ct)
    {
        _client = client;
        StreamName = ParseStreamName(client.StreamId);
        Id = StreamName ?? ZString.Format("SRT_{0}", client.PeerHandle);
    }

    public override async ValueTask BeginReceiveAsync(CancellationTokenSource readTimeoutSource)
    {
        var demuxer = new M2TSDemuxer();
        var adapter = new M2TSMediaFrameAdapter<SRTReceiverContext>(this);
        var packetCopy = new byte[M2TSWriter.PacketSize];
        var reader = RemoteReader;

        while (!IsCompleted)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ReadOnlySequence<byte> buff = result.Buffer;
            while (buff.Length >= M2TSWriter.PacketSize)
            {
                if (PeekByte(buff) != 0x47)
                {
                    // lost 188-byte alignment: skip to the next sync byte
                    SequencePosition? sync = buff.Slice(1).PositionOf((byte)0x47);
                    if (sync is null)
                    {
                        buff = buff.Slice(buff.End);
                        break;
                    }
                    Logger.ZLogWarning($"TS alignment lost; resynchronizing");
                    buff = buff.Slice(sync.Value);
                    continue;
                }

                ReadOnlySequence<byte> pktSeq = buff.Slice(0, M2TSWriter.PacketSize);
                if (pktSeq.IsSingleSegment)
                {
                    demuxer.ProcessPacket(pktSeq.FirstSpan, adapter);
                }
                else
                {
                    pktSeq.CopyTo(packetCopy);
                    demuxer.ProcessPacket(packetCopy, adapter);
                }
                buff = buff.Slice(M2TSWriter.PacketSize);
            }

            reader.AdvanceTo(buff.Start, result.Buffer.End);

            if (adapter.HasPendingFrames && MediaOutlet is not null)
            {
                adapter.HasPendingFrames = false;
                await MediaOutlet.FlushAsync(CancellationToken);
            }

            if (result.IsCompleted)
            {
                break;
            }
        }

        // emit whatever was still being assembled
        demuxer.Flush(adapter);
        if (adapter.HasPendingFrames && MediaOutlet is not null)
        {
            adapter.HasPendingFrames = false;
            await MediaOutlet.FlushAsync(CancellationToken);
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
