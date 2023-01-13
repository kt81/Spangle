using System.Buffers;
using System.IO.Pipelines;
using Cysharp.Text;
using Spangle.IO;
using Spangle.IO.Interop;

namespace Spangle.Rtmp.Chunk.Processor;

internal abstract class SetChunkSize : IChunkProcessor
{
    private const int ChunkSizeLength = 4; // 32bit Big Endian uint
    // private static ILogger<SetChunkSize> s_logger = SpangleLogManager.GetLogger<SetChunkSize>();

    public static async ValueTask PerformProcess(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) = await reader.ReadExactlyAsync(ChunkSizeLength, ct);
        uint size = BufferMarshal.As<BigEndianUInt32>(buff).HostValue;
        reader.AdvanceTo(buff.End);
        IChunkProcessor.EnsureValidProtocolControlMessage(context);

        if (size is 0 or > 0x0FFFFFFF)
        {
            throw new IOException(ZString.Format("Invalid chunk size: {0}", size));
        }

        context.MaxChunkSize = size;
        context.SetNext<BasicHeaderProcessor>();
    }
}
