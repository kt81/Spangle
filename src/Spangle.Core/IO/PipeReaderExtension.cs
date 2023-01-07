// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.IO.Pipelines;

namespace Spangle.IO;

public static class PipeReaderExtension
{
    public static ValueTask<(ReadOnlySequence<byte>, ReadResult)> ReadExactlyAsync(this PipeReader reader,
        int length, CancellationToken ct = default) =>
        ReadExactlyAsync(reader, length, 0, ct);

    public static async ValueTask<(ReadOnlySequence<byte>, ReadResult)> ReadExactlyAsync(this PipeReader reader, int length, int offset, CancellationToken ct = default)
    {
        var result = await reader.ReadAtLeastAsync(length + offset, ct);
        if (result.IsCanceled)
        {
            throw new OperationCanceledException();
        }
        return (result.SliceExactly(result.Buffer.GetPosition(offset), length), result);
    }
    
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
