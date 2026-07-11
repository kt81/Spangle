using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.M2TS;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

/// <summary>
/// HLS sender for MPEG-2 TS segments. Consumes a muxed TS stream from the intake pipe.
/// </summary>
public class HLSSender : ISender<HLSSenderContext>, IDisposable
{
    private static readonly ILogger<HLSSender> s_logger = SpangleLogManager.GetLogger<HLSSender>();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async ValueTask StartAsync(HLSSenderContext context)
    {
        var ct = context.CancellationToken;
        var reader = context.IntakeReader;
        HLSSegmenter? segmenter = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);

                if (segmenter is null && result.Buffer.Length > 0)
                {
                    // Media is flowing, so the stream name is known by now
                    string key = context.ResolveStreamKey();
                    HLSPlaylistHandover? resume = context.Registry?.TakeHandover(key);
                    Action<string, long, int>? onUpdated =
                        context.Registry is { } registry ? registry.GetOrAdd(key).Publish : null;
                    segmenter = new HLSSegmenter(context.ResolveStreamStorage(), context.TargetSegmentDuration,
                        resume, onUpdated);
                    s_logger.ZLogInformation($"HLS(TS) output for {key} to {context.StorageDescription}");
                }

                var consumed = segmenter is null
                    ? result.Buffer.Start
                    : ProcessBuffer(segmenter, result.Buffer);
                reader.AdvanceTo(consumed, result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            if (segmenter is not null)
            {
                if (context.EndBehavior == HLSEndBehavior.Handover && context.Registry is { } registry)
                {
                    // taken over: leave the playlist live for the successor session
                    registry.StashHandover(context.ResolveStreamKey(), segmenter.ExportHandover());
                    s_logger.ZLogInformation($"HLS stream handed over");
                }
                else
                {
                    segmenter.Complete();
                    // the ended playlist is served from storage; keeping the live entry
                    // would leak one registry slot per unique stream key
                    context.Registry?.Remove(context.ResolveStreamKey());
                    s_logger.ZLogInformation($"HLS stream completed");
                }
            }
        }
    }

    private static SequencePosition ProcessBuffer(HLSSegmenter segmenter, in ReadOnlySequence<byte> buffer)
    {
        var buff = buffer;
        Span<byte> copyBuff = stackalloc byte[M2TSWriter.PacketSize];
        while (buff.Length >= M2TSWriter.PacketSize)
        {
            var packetSeq = buff.Slice(0, M2TSWriter.PacketSize);
            if (packetSeq.IsSingleSegment)
            {
                segmenter.ProcessPacket(packetSeq.FirstSpan);
            }
            else
            {
                // The packet straddles a pipe segment boundary; copy is unavoidable
                packetSeq.CopyTo(copyBuff);
                segmenter.ProcessPacket(copyBuff);
            }
            buff = buff.Slice(M2TSWriter.PacketSize);
        }
        return buff.Start;
    }
}
