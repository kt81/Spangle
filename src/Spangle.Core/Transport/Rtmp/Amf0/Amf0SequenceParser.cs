using System.Buffers;
using System.Runtime.CompilerServices;
using Spangle.Interop;

namespace Spangle.Transport.Rtmp.Amf0;

internal static class Amf0SequenceParser
{
    /// <summary>
    /// Nesting bound for objects/arrays. AMF0 needs ~4 bytes per nesting level, so an
    /// unauthenticated connect message could otherwise drive the mutual recursion of
    /// <see cref="Parse(ref ReadOnlySequence{byte}, int)"/>/<see cref="ParseObject(ref ReadOnlySequence{byte}, int)"/>
    /// into a StackOverflowException —
    /// which is uncatchable and kills the whole process, not just the session.
    /// </summary>
    private const int MaxDepth = 32;

    public static object? Parse(ref ReadOnlySequence<byte> buff) => Parse(ref buff, 0);

    private static object? Parse(ref ReadOnlySequence<byte> buff, int depth)
    {
        if (buff.IsEmpty)
        {
            throw new InvalidDataException("Truncated AMF0 sequence");
        }
        var type = (Amf0TypeMarker)buff.FirstSpan[0];
        switch (type)
        {
            case Amf0TypeMarker.Number:
                return ParseNumber(ref buff);
            case Amf0TypeMarker.Boolean:
                return ParseBoolean(ref buff);
            case Amf0TypeMarker.String:
                return ParseString(ref buff);
            case Amf0TypeMarker.Object:
                return ParseObject(ref buff, depth);
            case Amf0TypeMarker.Null:
            case Amf0TypeMarker.Undefined:
                // Undefined carries no payload; like Null it decodes to the absence of a value
                return ParseNull(ref buff);
            case Amf0TypeMarker.EcmaArray:
                return ParseEcmaArray(ref buff, depth);
            case Amf0TypeMarker.ObjectEnd:
                return ParseObjectEnd(ref buff);
            case Amf0TypeMarker.StrictArray:
                return ParseStrictArray(ref buff, depth);
            case Amf0TypeMarker.Date:
                return ParseDate(ref buff);
            case Amf0TypeMarker.LongString:
            case Amf0TypeMarker.XmlDocument:
                // An XML document is wire-identical to a long string; only the marker differs
                return ParseLongString(ref buff);
            case Amf0TypeMarker.TypedObject:
                return ParseTypedObject(ref buff, depth);
            default:
                // Movieclip/Recordset (reserved, no defined payload), Reference (needs a
                // reference table) and AvmplusObject (switches to AMF3). Their length is
                // unknowable, so parsing cannot resume past them.
                throw new NotSupportedException($"The parser for {type} is not supported.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ParseNumber(ref ReadOnlySequence<byte> buff)
    {
        var s = buff.Slice(1, 8);
        buff = buff.Slice(s.End);
        return BufferMarshal.AsRefOrCopy<BigEndianDouble>(s).HostValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ParseBoolean(ref ReadOnlySequence<byte> buff)
    {
        var s = buff.Slice(1, 1);
        buff = buff.Slice(s.End);
        return s.FirstSpan[0] != 0;
    }

    /// <summary>
    /// Parse and create string from buffer
    /// </summary>
    /// <param name="buff"></param>
    /// <param name="isTypeMarkerIncluded">Set false for the string has no type-marker like anonymous object's key.</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ParseString(ref ReadOnlySequence<byte> buff, bool isTypeMarkerIncluded = true)
    {
        //  0                   1                   2                   3
        //  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
        // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
        // | Type (1 byte) | String Length (2 bytes)       | UTF-8 bytes...
        // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
        int pos = isTypeMarkerIncluded ? 1 : 0;
        const int lenLen = 2;

        var lenBuf = buff.Slice(pos, lenLen);
        ushort length = BufferMarshal.AsRefOrCopy<BigEndianUInt16>(lenBuf).HostValue;
        if (length == 0)
        {
            buff = buff.Slice(lenBuf.End);
            return string.Empty;
        }

        var strBuf = buff.Slice(pos + lenLen, length);
        buff = buff.Slice(strBuf.End);
        return BufferMarshal.Utf8ToManagedString(strBuf);
    }

    public static AmfObject ParseObject(ref ReadOnlySequence<byte> buff) => ParseObject(ref buff, 0);

    private static Dictionary<string, object?> ParseObject(ref ReadOnlySequence<byte> buff, int depth)
    {
        EnsureDepth(depth);
        buff = buff.Slice(1);
        return ParseObjectBody(ref buff, depth);
    }

    private static Dictionary<string, object?> ParseTypedObject(ref ReadOnlySequence<byte> buff, int depth)
    {
        EnsureDepth(depth);
        buff = buff.Slice(1);
        // The class name means nothing downstream (metadata ends up as JSON); the body is a
        // plain anonymous object.
        _ = ParseString(ref buff, false);
        return ParseObjectBody(ref buff, depth);
    }

    private static Dictionary<string, object?> ParseObjectBody(ref ReadOnlySequence<byte> buff, int depth)
    {
        var dic = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (!buff.IsEmpty)
        {
            string key = ParseString(ref buff, false);
            object? val = Parse(ref buff, depth + 1);
            if (val is Amf0TypeMarker.ObjectEnd)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    // ObjectEnd is only valid after the empty closing key
                    throw new InvalidDataException($"Malformed AMF0 object: ObjectEnd after key \"{key}\"");
                }

                break;
            }

            dic[key] = val;
        }

        return dic;
    }

    private static void EnsureDepth(int depth)
    {
        if (depth >= MaxDepth)
        {
            throw new InvalidDataException($"AMF0 nesting exceeds the limit of {MaxDepth}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Amf0TypeMarker ParseObjectEnd(ref ReadOnlySequence<byte> buff)
    {
        buff = buff.Slice(1);
        return Amf0TypeMarker.ObjectEnd;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? ParseNull(ref ReadOnlySequence<byte> buff)
    {
        buff = buff.Slice(1);
        return null;
    }

    private static object?[] ParseStrictArray(ref ReadOnlySequence<byte> buff, int depth)
    {
        EnsureDepth(depth);
        var s = buff.Slice(1, sizeof(uint));
        buff = buff.Slice(s.End);
        uint count = BufferMarshal.AsRefOrCopy<BigEndianUInt32>(s).HostValue;
        // Every element takes at least one byte, so a count beyond the remaining bytes is a
        // lie; checking before allocating keeps a tiny message from provoking a huge array.
        if (count > buff.Length)
        {
            throw new InvalidDataException($"StrictArray count {count} exceeds the remaining {buff.Length} bytes");
        }

        var values = new object?[count];
        for (uint i = 0; i < count; i++)
        {
            values[i] = Parse(ref buff, depth + 1);
        }

        return values;
    }

    public static DateTimeOffset ParseDate(ref ReadOnlySequence<byte> buff)
    {
        // Milliseconds since the epoch as a double, then a time-zone S16 the spec reserves as 0
        var s = buff.Slice(1, 8);
        double ms = BufferMarshal.AsRefOrCopy<BigEndianDouble>(s).HostValue;
        var tz = buff.Slice(9, 2);
        buff = buff.Slice(tz.End);

        // DateTimeOffset's own range, so FromUnixTimeMilliseconds cannot throw on wire data
        const double minMs = -62_135_596_800_000;
        const double maxMs = 253_402_300_799_999;
        if (!double.IsFinite(ms) || ms is < minMs or > maxMs)
        {
            throw new InvalidDataException($"AMF0 Date is out of range: {ms}");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
    }

    public static string ParseLongString(ref ReadOnlySequence<byte> buff)
    {
        const int lenLen = sizeof(uint);
        var lenBuf = buff.Slice(1, lenLen);
        uint length = BufferMarshal.AsRefOrCopy<BigEndianUInt32>(lenBuf).HostValue;
        if (length > buff.Length)
        {
            throw new InvalidDataException($"LongString length {length} exceeds the remaining {buff.Length} bytes");
        }

        if (length == 0)
        {
            buff = buff.Slice(lenBuf.End);
            return string.Empty;
        }

        var strBuf = buff.Slice(1 + lenLen, length);
        buff = buff.Slice(strBuf.End);
        return BufferMarshal.Utf8ToManagedString(strBuf);
    }

    public static AmfObject ParseEcmaArray(ref ReadOnlySequence<byte> buff) => ParseEcmaArray(ref buff, 0);

    private static Dictionary<string, object?> ParseEcmaArray(ref ReadOnlySequence<byte> buff, int depth)
    {
        EnsureDepth(depth);
        var s = buff.Slice(1, sizeof(uint));
        buff = buff.Slice(s.End);
        uint count = BufferMarshal.AsRefOrCopy<BigEndianUInt32>(s).HostValue;

        var dic = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            string key = ParseString(ref buff, false);
            object? val = Parse(ref buff, depth + 1);
            dic[key] = val;
        }
        if (buff.IsEmpty)
        {
            return dic;
        }

        // Check count + 1 tokens
        string endKey = ParseString(ref buff, false);
        if (!string.IsNullOrEmpty(endKey))
        {
            // Broken
            throw new InvalidDataException($"Malformed EcmaArray close-part: {endKey}");
        }
        ParseObjectEnd(ref buff);

        return dic;
    }

}
