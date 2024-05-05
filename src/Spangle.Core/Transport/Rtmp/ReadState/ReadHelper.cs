using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Spangle.IO;
using Spangle.Logging;
using Spangle.Spinner;
using Spangle.Transport.Rtmp.Chunk;
using ZLogger;

namespace Spangle.Transport.Rtmp.ReadState;

internal class ReadHelper
{
    private static readonly ILogger<ReadHelper> s_logger;
    static ReadHelper()
    {
        s_logger = SpangleLogManager.GetLogger<ReadHelper>();
    }

    /// <summary>
    /// Read chunked long sequence.
    /// <see cref="PipeReader.AdvanceTo(System.SequencePosition)"/> must be called for each iteration.
    /// </summary>
    /// <param name="context"></param>
    /// <returns>Individual chunk body</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async IAsyncEnumerable<ReadOnlySequence<byte>> ReadChunkedMessageBody(RtmpReceiverContext context)
    {
        var reader = context.RemoteReader;
        uint readingChunkStreamId = context.BasicHeader.ChunkStreamId;
        // Total length to read with this method (header length is not included)
        uint totalLength = context.MessageHeader.Length.HostValue;
        // Length to be read at once
        uint chunkLength = Math.Min(totalLength, context.ChunkSize);
        var shouldConsumeHeader = false;

        while (totalLength > 0)
        {
            s_logger.ZLogTrace($"Total length: {totalLength}, Chunk length: {chunkLength}");

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
            // Whole a chunk is read, so it should be consumed header next time
            shouldConsumeHeader = true;

            yield return result;
        }
    }

    /// <summary>
    /// Read message body.
    /// If it is chunked, Create new ReadOnlySequence from copy of original buffer.
    /// Don't use this for Video and Audio messages.
    /// </summary>
    /// <param name="context"></param>
    /// <returns>Original or combined buffer and <see cref="BufferDisposeHandle"/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<(ReadOnlySequence<byte>, BufferDisposeHandle)> ReadMessageBody(RtmpReceiverContext context)
    {
        var msgLen = (int)context.MessageHeader.Length.HostValue;

        if (msgLen <= context.ChunkSize)
        {
            // Single chunk
            (ReadOnlySequence<byte> b, _) = await context.RemoteReader.ReadExactAsync(msgLen, context.CancellationToken);
            return (b, default);
        }

        // Multiple chunks, reconstruct buffer
        var chunks = ReadChunkedMessageBody(context).GetAsyncEnumerator(context.CancellationToken);
        await chunks.MoveNextAsync();
        var first = chunks.Current.ToMemorySegment();
        var last = first;
        while (await chunks.MoveNextAsync())
        {
            last = last.Append(chunks.Current.ToArray());
        }

        return (new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length), new BufferDisposeHandle(first));
    }

    /// <summary>
    /// Handle to dispose all segments returned by <see cref="ReadHelper.ReadMessageBody"/>.
    /// </summary>
    public readonly struct BufferDisposeHandle : IDisposable
    {
        private readonly IDisposable? _disposable;

        public BufferDisposeHandle(IDisposable disposable)
        {
            _disposable = disposable;
        }

        /// <summary>
        /// Indicates that the caller should call <see cref="PipeReader.AdvanceTo(SequencePosition)"/> or not.
        /// </summary>
        public bool IsOriginalBufferConsumed => _disposable != null;

        public void Dispose()
        {
            _disposable?.Dispose();
        }
    }
}
