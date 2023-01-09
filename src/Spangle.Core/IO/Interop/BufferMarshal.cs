using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.IO.Interop;

/// <summary>
/// Marshaling from ReadOnlySequence buffer
/// </summary>
public static class BufferMarshal
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TMessage As<TMessage>(in ReadOnlySequence<byte> buff) where TMessage : unmanaged
    {
        Span<byte> copied = new byte[Marshal.SizeOf<TMessage>()];
        buff.CopyTo(copied);
        return ref MemoryMarshal.AsRef<TMessage>(copied);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly TMessage AsRefOrCopy<TMessage>(in ReadOnlySequence<byte> buff) where TMessage : unmanaged
    {
        if (buff.IsSingleSegment)
        {
            return ref MemoryMarshal.AsRef<TMessage>(buff.FirstSpan);
        }

        return ref As<TMessage>(buff);
    }
}
