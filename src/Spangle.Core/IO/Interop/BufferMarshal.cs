using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Spangle.Util;

namespace Spangle.IO.Interop;

/// <summary>
/// Marshaling from ReadOnlySequence buffer
/// </summary>
public static class BufferMarshal
{
    /// <summary>
    /// Create unmanaged instance from byte sequence.
    /// </summary>
    /// <param name="buff"></param>
    /// <typeparam name="TMessage"></typeparam>
    /// <returns></returns>
    /// <remarks>
    /// Unlike <see cref="AsRefOrCopy{TMessage}"/>, the instance is always allocated on another memory than original buffer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TMessage As<TMessage>(in ReadOnlySequence<byte> buff) where TMessage : unmanaged
    {
        Span<byte> copied = new byte[MarshalHelper<TMessage>.Size];
        buff.CopyTo(copied);
        return ref MemoryMarshal.AsRef<TMessage>(copied);
    }

    /// <summary>
    /// Map buffer or create unmanaged instance from byte sequence.
    /// </summary>
    /// <param name="buff"></param>
    /// <typeparam name="TMessage">The unmanaged type to map</typeparam>
    /// <returns></returns>
    /// <remarks>
    /// The instance is referencing original buffer if possible and MUST be consumed before the buffer is lost.
    /// If the buffer came from PipeReader, the call to `PipeReader.AdvanceTo()` MUST be deferred until the instance is no longer needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly TMessage AsRefOrCopy<TMessage>(in ReadOnlySequence<byte> buff) where TMessage : unmanaged
    {
        if (buff.IsSingleSegment)
        {
            return ref MemoryMarshal.AsRef<TMessage>(buff.FirstSpan);
        }

        return ref As<TMessage>(buff);
    }

    public static string Utf8ToManagedString(in ReadOnlySequence<byte> buff)
    {
        ReadOnlySpan<byte> strBuf;
        if (buff.IsSingleSegment)
        {
            strBuf = buff.FirstSpan;
        }
        else
        {
            Span<byte> b = new byte[buff.Length];
            buff.CopyTo(b);
            strBuf = b;
        }

        return Encoding.UTF8.GetString(strBuf);
    }
}
