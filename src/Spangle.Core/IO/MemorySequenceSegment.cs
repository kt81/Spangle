using System.Buffers;
using Microsoft.Extensions.ObjectPool;

namespace Spangle.IO;

internal sealed class MemorySequenceSegment : ReadOnlySequenceSegment<byte>, IDisposable
{
    private static readonly ObjectPool<MemorySequenceSegment> s_selfPool;

    static MemorySequenceSegment()
    {
        s_selfPool = new DefaultObjectPool<MemorySequenceSegment>(new DefaultPooledObjectPolicy<MemorySequenceSegment>());
    }

    /// <summary>
    /// Do not call directory
    /// </summary>
    public MemorySequenceSegment()
    {
    }

    public static MemorySequenceSegment Get(ReadOnlyMemory<byte> memory)
    {
        var self = s_selfPool.Get();
        self.Memory = memory;
        return self;
    }

    public MemorySequenceSegment Append(ReadOnlyMemory<byte> memory)
    {
        var sibling = Get(memory);
        sibling.RunningIndex = RunningIndex + Memory.Length;
        Next = sibling;
        return sibling;
    }

    public void Dispose()
    {
        Memory = null;
        if (Next is MemorySequenceSegment sibling)
        {
            sibling.Dispose();
        }
        Next = null;
        RunningIndex = 0;
        s_selfPool.Return(this);
    }
}

internal static class ReadonlySequenceExtensions
{
    public static MemorySequenceSegment ToMemorySegment(this ReadOnlySequence<byte> buff)
    {
        return MemorySequenceSegment.Get(buff.ToArray());
    }
}
