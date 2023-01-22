using System.IO.Pipelines;
using System.Text;
using Spangle.IO.Interop;
using Spangle.Util;

namespace Spangle.Rtmp.Amf0;

/// <summary>
/// Amf0Writer writes the host typed value to PipeWriter in AMF0.
/// </summary>
/// <remarks>
/// The methods described in this class are not very concerned with speed because they are called very infrequently.
/// </remarks>
internal static class Amf0Writer
{
    private const int MarkerLength = 1;

    public static int Write(PipeWriter writer, object? value, bool requiresTypeMarker = true)
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
            case IReadOnlyDictionary<string, object?> dic:
                return WriteObject(writer, dic);
            case null:
                return WriteNull(writer);
            default:
                throw new NotImplementedException($"The encoder for {value.GetType().Name} is not implemented.");
        }
    }

    public static int WriteNumber(PipeWriter writer, double value)
    {
        const int totalLen = sizeof(double) + MarkerLength;
        var result = writer.GetSpan(totalLen);
        result[0] = (byte)Amf0TypeMarker.Number;
        BigEndianDouble.FromHost(value).ToBytes().CopyTo(result[1..]);

        writer.Advance(totalLen);
        return totalLen;
    }

    public static int WriteBoolean(PipeWriter writer, bool value)
    {
        const int totalLen = sizeof(bool) + MarkerLength;
        var result = writer.GetSpan(totalLen);
        result[0] = (byte)Amf0TypeMarker.Boolean;
        result[1] = (byte)(value ? 1 : 0);

        writer.Advance(totalLen);
        return totalLen;
    }

    public static int WriteString(PipeWriter writer, string value, bool requiresTypeMarker = true)
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

        // Write length header
        result[pos++] = (byte)(stringLen >>> 8);
        result[pos++] = (byte)stringLen;
        // Write string body
        Encoding.UTF8.GetBytes(value, result.Slice(pos, stringLen));

        writer.Advance(totalLen);

        return totalLen;
    }

    public static int WriteObject(PipeWriter writer, IReadOnlyDictionary<string, object?> dic)
    {
        // object-marker
        int totalLen = MarkerLength;
        var buff = writer.GetSpan(MarkerLength);
        buff[0] = (byte)Amf0TypeMarker.Object;
        writer.Advance(MarkerLength);

        // object-property
        totalLen += dic.Sum(pair =>
            WriteString(writer, pair.Key, false)
            + Write(writer, pair.Value));

        // object-end
        const int endSize = 3;
        buff = writer.GetSpan(endSize);
        buff[0] = buff[1] = 0; // UTF-8-empty
        buff[2] = (byte)Amf0TypeMarker.ObjectEnd;
        writer.Advance(endSize);
        totalLen += endSize;

        return totalLen;
    }

    public static int WriteNull(PipeWriter writer)
    {
        var result = writer.GetSpan(MarkerLength);
        result[0] = (byte)Amf0TypeMarker.Null;
        writer.Advance(MarkerLength);
        return MarkerLength;
    }
}
