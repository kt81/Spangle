using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;
using Spangle.Rtmp.Chunk;

namespace Spangle.Rtmp.ReadState;

internal static class ReadHelper
{
    /// <summary>
    /// Read chunked long sequence.
    /// <see cref="PipeReader.AdvanceTo(System.SequencePosition)"/> must be called for each iteration.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async IAsyncEnumerable<ReadOnlySequence<byte>> ReadChunkedSequence(RtmpReceiverContext context)
    {
        var reader = context.Reader;
        uint readingChunkStreamId = context.BasicHeader.ChunkStreamId;
        // Total length to read with this method (header length is not included)
        uint totalLength = context.MessageHeader.Length.HostValue;
        // Length to be read at once
        uint chunkLength = Math.Min(totalLength, context.ChunkSize);
        var shouldConsumeHeader = false;

        while (totalLength > 0)
        {
            if (shouldConsumeHeader)
            {
                await ReadChunkHeader.Perform(context);
                if (context.BasicHeader.Format != MessageHeaderFormat.Fmt3
                    || context.BasicHeader.ChunkStreamId != readingChunkStreamId)
                {
                    throw new InvalidOperationException("panic " + context.BasicHeader);
                }
            }

            if (totalLength < chunkLength)
            {
                chunkLength = totalLength;
            }

            (ReadOnlySequence<byte> result, _) = await reader.ReadExactAsync((int)chunkLength);
            totalLength -= chunkLength;
            shouldConsumeHeader = true;

            yield return result;
        }
    }
}
