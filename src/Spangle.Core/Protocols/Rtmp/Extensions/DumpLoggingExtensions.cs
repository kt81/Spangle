using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Spangle.Protocols.Rtmp.Extensions;

internal static class DumpLoggingExtensions
{
    [Conditional("DEBUG")]
    public static void DumpObject(this ILogger logger, AmfObject? anonObject,
        [CallerArgumentExpression("anonObject")]
        string? name = null)
    {
        logger.ZLogDebug("[{0}]:{1}", name, System.Text.Json.JsonSerializer.Serialize(anonObject));
    }
}
