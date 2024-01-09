using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Spangle.Transport.Rtmp.Extensions;

internal static class DumpLoggingExtensions
{
    [Conditional("DEBUG")]
    public static void DumpObject(this ILogger logger, AmfObject? anonObject,
        [CallerArgumentExpression("anonObject")]
        string? name = null)
    {
        logger.ZLogDebug($"[{name}]:{System.Text.Json.JsonSerializer.Serialize(anonObject)}");
    }
}
