using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Spangle.IO;

public static class PipeReaderExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<(ReadOnlySequence<byte>, ReadResult)> ReadExactAsync(this PipeReader reader,
        int length, CancellationToken ct = default) =>
        ReadExactAsync(reader, length, 0, ct);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<(ReadOnlySequence<byte>, ReadResult)> ReadExactAsync(this PipeReader reader, int length, int offset, CancellationToken ct = default)
    {
        var result = await reader.ReadAtLeastAsync(length + offset, ct);
        if (result.IsCanceled)
        {
            throw new OperationCanceledException();
        }
        return (result.SliceExactly(result.Buffer.GetPosition(offset), length), result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySequence<byte> SliceExactly(this ReadResult result, SequencePosition start, long expectedLength)
    {
        var buff = result.Buffer;
        if (buff.Length < expectedLength)
        {
            throw new ArgumentOutOfRangeException(nameof(result.Buffer), "ReadResult must be retrieved using the method that kind of ReadAtLeast*.");
        }
        return buff.Slice(start, expectedLength);
    }

}
