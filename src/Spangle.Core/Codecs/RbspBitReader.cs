namespace Spangle.Codecs;

/// <summary>
/// MSB-first bit reader with Exp-Golomb support, shared by the H.264/H.265
/// parameter-set parsers. Operates on unescaped RBSP bytes.
/// </summary>
internal ref struct RbspBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPos;

    public bool ReadBit()
    {
        int byteIndex = _bitPos >> 3;
        if (byteIndex >= _data.Length)
        {
            throw new InvalidDataException("RBSP ended before the parse did");
        }
        int bitIndex = 7 - (_bitPos & 7);
        _bitPos++;
        return ((_data[byteIndex] >> bitIndex) & 1) != 0;
    }

    public ulong ReadBits(int count)
    {
        ulong value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | (ReadBit() ? 1UL : 0UL);
        }
        return value;
    }

    /// <summary>ue(v): unsigned Exp-Golomb</summary>
    public ulong ReadUe()
    {
        var leadingZeros = 0;
        while (!ReadBit())
        {
            leadingZeros++;
            if (leadingZeros > 32)
            {
                throw new InvalidDataException("Broken Exp-Golomb code");
            }
        }
        return (1UL << leadingZeros) - 1 + ReadBits(leadingZeros);
    }

    /// <summary>se(v): signed Exp-Golomb</summary>
    public long ReadSe()
    {
        ulong k = ReadUe();
        return (k & 1) != 0 ? (long)((k + 1) >> 1) : -(long)(k >> 1);
    }

    /// <summary>Removes emulation prevention bytes (00 00 03 → 00 00).</summary>
    public static int Unescape(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        var written = 0;
        var zeros = 0;
        for (var i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue;
            }
            zeros = b == 0 ? zeros + 1 : 0;
            dest[written++] = b;
        }
        return written;
    }
}
