using System.Buffers;
using System.IO.Pipelines;
using Cysharp.Text;
using Spangle.Interop;
using Spangle.IO;

namespace Spangle.Transport.Rtmp.ReadState;

internal abstract class SetChunkSize : IReadStateAction
{
    private const int ChunkSizeLength = 4; // 32bit Big Endian uint
    // private static ILogger<SetChunkSize> s_logger = SpangleLogManager.GetLogger<SetChunkSize>();

    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.RemoteReader;
        CancellationToken ct = context.CancellationToken;
        (ReadOnlySequence<byte> buff, _) = await reader.ReadExactAsync(ChunkSizeLength, ct);
        uint size = BufferMarshal.As<BigEndianUInt32>(buff).HostValue;
        reader.AdvanceTo(buff.End);
        IReadStateAction.EnsureValidProtocolControlMessage(context);

        if (size is < Protocol.MinChunkSize or > Protocol.MaxChunkSize)
        {
            throw new InvalidOperationException(ZString.Format("Invalid chunk size: {0}", size));
        }

        context.ChunkSize = size;
        context.SetNext<ReadChunkHeader>();
    }
}
