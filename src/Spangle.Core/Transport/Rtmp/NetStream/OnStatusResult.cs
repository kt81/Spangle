using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Transport.Rtmp.NetStream;

[Amf0Serializable]
internal partial struct OnStatusResult
{
    [Amf0Field(0)] public readonly string     CommandName   = "onStatus";
    [Amf0Field(1)] public readonly double     TransactionId = 0.0;
    [Amf0Field(2)] public readonly AmfObject? CommandObject = null;
    [Amf0Field(3)] public          AmfObject  Information;

    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    private static class InfoKeys
    {
        public const string Level       = "level";
        public const string Code        = "code";
        public const string Description = "description";
        public const string Details     = "details";
    }

    public enum Level
    {
        Warning = 1,
        Status,
        Error,
    }

    public enum Code
    {
        Play = 1,
        Publish,
        Send,
    }

    public OnStatusResult(Level level, Code code, string description, string details)
    {
        var infoObj = new Dictionary<string, object?>
        {
            [InfoKeys.Level] = level.ToString().ToLowerInvariant(),
            [InfoKeys.Code] = code.ToResponseString(),
            [InfoKeys.Description] = description,
            [InfoKeys.Details] = details,
        };
        Information = infoObj;
    }
}

internal static class CodeExtension
{
    public static string ToResponseString(this OnStatusResult.Code code)
    {
        return code switch
        {
            OnStatusResult.Code.Play    => "NetStream.Play.Start",
            OnStatusResult.Code.Publish => "NetStream.Publish.Start",
            OnStatusResult.Code.Send    => "NetStream.Send.Start",

            _ => throw new InvalidEnumArgumentException(nameof(code), (int)code, code.GetType())
        };
    }
}
