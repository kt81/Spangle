using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Spangle.IO;
using Spangle.IO.Interop;
using Spangle.Rtmp.Chunk;

namespace Spangle.Rtmp.ReadState;

/// <summary>
///     ReadBasicHeader
///     <see cref="BasicHeader" />
/// </summary>
internal abstract class ReadBasicHeader : IReadStateAction
{
    public static async ValueTask Perform(RtmpReceiverContext context)
    {
        PipeReader reader = context.Reader;
        CancellationToken ct = context.CancellationToken;

        // Check the first byte
        (ReadOnlySequence<byte> firstBuff, _) = await reader.ReadExactlyAsync(1, ct);
        firstBuff.CopyTo(context.BasicHeader.ToSpan());
        var endPos = firstBuff.End;

        // Read the remaining buffer if needed
        int fullLen = context.BasicHeader.RequiredLength;
        if (fullLen > 1)
        {
            (ReadOnlySequence<byte> fullBuff, _) = await reader.ReadExactlyAsync(fullLen, ct);
            fullBuff.CopyTo(context.BasicHeader.ToSpan());
            endPos = fullBuff.End;
        }

        reader.AdvanceTo(endPos);

        context.SetNext<ReadMessageHeader>();
    }
}
