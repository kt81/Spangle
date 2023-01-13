using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spangle.Rtmp.Logging;

public static class SpangleLogManager
{
    private static ILogger s_globalLogger;
    private static ILoggerFactory s_loggerFactory;

    static SpangleLogManager()
    {
        SetLoggerFactory(NullLoggerFactory.Instance);
    }

    public static void SetLoggerFactory(ILoggerFactory loggerFactory)
    {
        s_loggerFactory = loggerFactory;
        s_globalLogger = loggerFactory.CreateLogger("Spangle");
    }

    internal static ILogger Logger => s_globalLogger;

    internal static ILogger<T> GetLogger<T>() where T : class => s_loggerFactory.CreateLogger<T>();
    internal static ILogger GetLogger(string categoryName) => s_loggerFactory.CreateLogger(categoryName);
}
