using System.Buffers;
using System.Text;
using Spangle.Interop;
using Spangle.Util;

namespace Spangle.Transport.Rtmp.Amf0;

/// <summary>
/// Amf0Writer writes the host typed value to IBufferWriter`byte in AMF0.
/// </summary>
/// <remarks>
/// The methods described in this class are not very concerned with speed because they are called very infrequently.
/// </remarks>
internal static class Amf0Writer
{
    private const int MarkerLength = 1;

    public static int Write(IBufferWriter<byte> writer, object? value, bool requiresTypeMarker = true)
    {
        switch (value)
        {
            // Subset of all IConvertible
            case byte or sbyte
                or ushort or short
                or uint or int
                or ulong or long
                or decimal or double or float:
                return WriteNumber(writer, Convert.ToDouble(value));
            case bool b:
                return WriteBoolean(writer, b);
            case string s:
                return WriteString(writer, s, requiresTypeMarker);
            case AmfObject dic:
                return WriteObject(writer, dic);
            case null:
                return WriteNull(writer);
            default:
                throw new NotImplementedException($"The encoder for {value.GetType().Name} is not implemented.");
        }
    }

    public static int WriteNumber(IBufferWriter<byte> writer, double value)
    {
        const int totalLen = sizeof(double) + MarkerLength;
        var result = writer.GetSpan(totalLen);
        result[0] = (byte)Amf0TypeMarker.Number;
        BigEndianDouble.FromHost(value).AsSpan().CopyTo(result[1..]);

        writer.Advance(totalLen);
        return totalLen;
    }

    public static int WriteBoolean(IBufferWriter<byte> writer, bool value)
    {
        const int totalLen = sizeof(bool) + MarkerLength;
        var result = writer.GetSpan(totalLen);
        result[0] = (byte)Amf0TypeMarker.Boolean;
        result[1] = (byte)(value ? 1 : 0);

        writer.Advance(totalLen);
        return totalLen;
    }

    public static int WriteString(IBufferWriter<byte> writer, string value, bool requiresTypeMarker = true)
    {
        return WriteUTF8String(writer, Encoding.UTF8.GetBytes(value), requiresTypeMarker);
    }

    public static int WriteUTF8String(IBufferWriter<byte> writer, ReadOnlySpan<byte> value, bool requiresTypeMarker = true)
    {
        int stringLen = value.Length;
        if (stringLen > ushort.MaxValue)
        {
            // Amf0TypeMarker.LongString
            ThrowHelper.ThrowOverSpec();
        }

        int totalLen, pos = 0;
        Span<byte> result;
        if (requiresTypeMarker)
        {
            totalLen = stringLen + sizeof(ushort) + MarkerLength;
            result = writer.GetSpan(totalLen);
            result[pos++] = (byte)Amf0TypeMarker.String;
        }
        else
        {
            totalLen = stringLen + sizeof(ushort);
            result = writer.GetSpan(totalLen);
        }

        // Write length header
        result[pos++] = (byte)(stringLen >>> 8);
        result[pos++] = (byte)stringLen;
        // Write string body
        value.CopyTo(result.Slice(pos, stringLen));

        writer.Advance(totalLen);

        return totalLen;
    }

    public static int WriteObjectHeader(IBufferWriter<byte> writer)
    {
        var buff = writer.GetSpan(MarkerLength);
        buff[0] = (byte)Amf0TypeMarker.Object;
        writer.Advance(MarkerLength);
        return 1;
    }

    public static int WriteObjectEnd(IBufferWriter<byte> writer)
    {
        const int endSize = 3;
        var buff = writer.GetSpan(endSize);
        buff[0] = buff[1] = 0; // UTF-8-empty
        buff[2] = (byte)Amf0TypeMarker.ObjectEnd;
        writer.Advance(endSize);
        return endSize;
    }

    public static int WriteObject(IBufferWriter<byte> writer, AmfObject? dic)
    {
        if (dic is null)
        {
            return WriteNull(writer);
        }

        // object-marker
        int totalLen = WriteObjectHeader(writer);

        // object-property
        totalLen += dic.Sum(pair =>
            WriteString(writer, pair.Key, false)
            + Write(writer, pair.Value));

        // object-end
        totalLen += WriteObjectEnd(writer);

        return totalLen;
    }

    public static int WriteNull(IBufferWriter<byte> writer)
    {
        var result = writer.GetSpan(MarkerLength);
        result[0] = (byte)Amf0TypeMarker.Null;
        writer.Advance(MarkerLength);
        return MarkerLength;
    }

    public static ReadOnlyMemory<byte> PrepareUTF8String(ReadOnlySpan<byte> utf8String)
    {
        var writer = new ArrayBufferWriter<byte>(utf8String.Length + MarkerLength + sizeof(ushort));
        WriteUTF8String(writer, utf8String);
        return writer.WrittenMemory;
    }
}
