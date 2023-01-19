using System.Buffers;
using System.Text;
using Spangle.IO.Interop;
using Spangle.Util;

namespace Spangle.Rtmp.Amf0;

internal static class Amf0Writer
{
    private const int MarkerLength           = 1;

    public static Amf0TypeMarker DetermineAmf0Type(IBufferWriter<byte> writer, object? value)
    {
        switch (value)
        {
            // Subset of all IConvertible
            case byte or sbyte
                or ushort or short
                or uint or int
                or ulong or long
                or decimal or double or float:
                return Amf0TypeMarker.Number;
            case bool:
                return Amf0TypeMarker.Boolean;
            case string s:
                return Amf0TypeMarker.String;
            case null:
                return Amf0TypeMarker.Null;
            default:
                throw new NotImplementedException($"The encoder for {value.GetType().Name} is not implemented.");
        }
    }

    public static int WriteNumber(IBufferWriter<byte> writer, double value)
    {
        const int totalLen = sizeof(double) + MarkerLength;
        var result = writer.GetSpan(totalLen);
        result[0] = (byte)Amf0TypeMarker.Number;
        BigEndianDouble.FromHost(value).ToBytes().CopyTo(result[1..]);

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
        int stringLen = Encoding.UTF8.GetByteCount(value);
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

        result[pos++] = (byte)(stringLen >>> 8);
        result[pos++] = (byte)stringLen;
        Encoding.UTF8.GetBytes(value, result.Slice(pos, stringLen));

        writer.Advance(totalLen);

        return totalLen;
    }

    public static int WriteNull(IBufferWriter<byte> writer)
    {
        var result = writer.GetSpan(MarkerLength);
        result[0] = (byte)Amf0TypeMarker.Null;
        writer.Advance(MarkerLength);
        return MarkerLength;
    }
}
