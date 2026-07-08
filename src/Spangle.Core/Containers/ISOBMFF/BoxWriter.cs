using System.Buffers.Binary;
using System.Text;

namespace Spangle.Containers.ISOBMFF;

/// <summary>
/// Sequential ISO-BMFF box writer. <see cref="Begin"/>/<see cref="End"/> nest boxes and
/// backpatch the 32-bit size field automatically.
/// </summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream _ms = new();
    private readonly Stack<long> _openBoxes = new();
    private readonly byte[] _scratch = new byte[8];

    public long Length => _ms.Length;

    /// <summary>Rewinds the writer for reuse; the underlying buffer is kept</summary>
    public void Reset()
    {
        _ms.SetLength(0);
        _openBoxes.Clear();
    }

    /// <summary>Writes the accumulated bytes to <paramref name="destination"/> without copying to an intermediate array</summary>
    public void WriteTo(Stream destination)
    {
        destination.Write(_ms.GetBuffer(), 0, (int)_ms.Length);
    }

    public void Begin(string type)
    {
        _openBoxes.Push(_ms.Position);
        WriteUInt32(0); // size, patched in End()
        WriteFourCc(type);
    }

    /// <summary>Begins a FullBox (version + 24-bit flags)</summary>
    public void BeginFull(string type, byte version, uint flags)
    {
        Begin(type);
        WriteUInt32(((uint)version << 24) | (flags & 0xFFFFFF));
    }

    public void End()
    {
        long start = _openBoxes.Pop();
        long end = _ms.Position;
        _ms.Position = start;
        WriteUInt32((uint)(end - start));
        _ms.Position = end;
    }

    public void WriteFourCc(string type)
    {
        Encoding.ASCII.GetBytes(type, _scratch);
        _ms.Write(_scratch, 0, 4);
    }

    public void WriteUInt8(byte value) => _ms.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_scratch, value);
        _ms.Write(_scratch, 0, 2);
    }

    public void WriteInt16(short value) => WriteUInt16((ushort)value);

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_scratch, value);
        _ms.Write(_scratch, 0, 4);
    }

    public void WriteInt32(int value) => WriteUInt32((uint)value);

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_scratch, value);
        _ms.Write(_scratch, 0, 8);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _ms.Write(bytes);

    public void WriteZeros(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _ms.WriteByte(0);
        }
    }

    /// <summary>Writes a placeholder u32 and returns its position for later patching</summary>
    public long ReserveUInt32()
    {
        long position = _ms.Position;
        WriteUInt32(0);
        return position;
    }

    public void PatchUInt32(long position, uint value)
    {
        long current = _ms.Position;
        _ms.Position = position;
        WriteUInt32(value);
        _ms.Position = current;
    }

    public byte[] ToArray() => _ms.ToArray();
}
