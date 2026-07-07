using System.Buffers;
using Microsoft.Extensions.Logging;
using Spangle.Containers.M2TS;
using Spangle.Logging;
using ZLogger;

namespace Spangle.Transport.HLS;

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
        var segmenter = new HLSSegmenter(context.OutputDirectory, context.TargetSegmentDuration);
        var reader = context.VideoReader;
        s_logger.ZLogInformation($"HLS output to {context.OutputDirectory}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);

                var consumed = ProcessBuffer(segmenter, result.Buffer);
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
            segmenter.Complete();
            s_logger.ZLogInformation($"HLS stream completed");
        }
    }

    private static SequencePosition ProcessBuffer(HLSSegmenter segmenter, in ReadOnlySequence<byte> buffer)
    {
        var buff = buffer;
        Span<byte> packet = stackalloc byte[M2TSWriter.PacketSize];
        while (buff.Length >= M2TSWriter.PacketSize)
        {
            var packetSeq = buff.Slice(0, M2TSWriter.PacketSize);
            packetSeq.CopyTo(packet);
            segmenter.ProcessPacket(packet);
            buff = buff.Slice(M2TSWriter.PacketSize);
        }
        return buff.Start;
    }
}
