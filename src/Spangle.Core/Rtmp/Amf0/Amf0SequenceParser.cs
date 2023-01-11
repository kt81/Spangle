using System.Buffers;
using System.Runtime.CompilerServices;
using Spangle.IO.Interop;

namespace Spangle.Rtmp.Amf0;

internal static class Amf0SequenceParser
{
    public static object Parse(ref ReadOnlySequence<byte> buff)
    {
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
                return ParseObject(ref buff);
            case Amf0TypeMarker.ObjectEnd:
                return ParseObjectEnd(ref buff);
            default:
                throw new NotImplementedException($"The parser for {type} is not implemented.");
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

    public static IReadOnlyDictionary<string, object> ParseObject(ref ReadOnlySequence<byte> buff)
    {
        buff = buff.Slice(1);
        var dic = new Dictionary<string, object>();
        while (!buff.IsEmpty)
        {
            string key = ParseString(ref buff, false);
            object val = Parse(ref buff);
            if (key == string.Empty && val is Amf0TypeMarker.ObjectEnd)
            {
                break;
            }

            dic[key] = val;
        }

        return dic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Amf0TypeMarker ParseObjectEnd(ref ReadOnlySequence<byte> buff)
    {
        buff = buff.Slice(1);
        return Amf0TypeMarker.ObjectEnd;
    }

}
