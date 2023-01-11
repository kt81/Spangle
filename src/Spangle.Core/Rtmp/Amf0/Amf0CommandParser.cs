using System.Buffers;
using static Spangle.Rtmp.Amf0.Amf0SequenceParser;

namespace Spangle.Rtmp.Amf0;

/// <summary>
/// Get Amf0Command from buffer
/// </summary>
/// <remarks>This class modifies the position of passed ReadOnlySequence</remarks>
internal static class Amf0CommandParser
{
    public static void ParseCommand(ref ReadOnlySequence<byte> buff)
    {
        string command = ParseString(ref buff);
        double transactionId = ParseNumber(ref buff);
        IReadOnlyDictionary<string, object> obj = ParseObject(ref buff);
    }






}
