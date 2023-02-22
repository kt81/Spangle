using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
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
        context.PreviousFormat = context.BasicHeader.Format;

        // Check the first byte
        (ReadOnlySequence<byte> firstBuff, _) = await reader.ReadExactlyAsync(1, ct);
        firstBuff.CopyTo(context.BasicHeader.AsSpan());
        var endPos = firstBuff.End;

        // Read the remaining buffer if needed
        int fullLen = context.BasicHeader.RequiredLength;
        if (fullLen > 1)
        {
            reader.AdvanceTo(firstBuff.Start); // reset reading
            (ReadOnlySequence<byte> fullBuff, _) = await reader.ReadExactlyAsync(fullLen, ct);
            fullBuff.CopyTo(context.BasicHeader.AsSpan());
            endPos = fullBuff.End;
        }

        reader.AdvanceTo(endPos);

        context.SetNext<ReadMessageHeader>();
    }
}
