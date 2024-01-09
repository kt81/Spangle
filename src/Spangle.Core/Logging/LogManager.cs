using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spangle.Logging;

public static class SpangleLogManager
{
    private static ILoggerFactory s_loggerFactory = new NullLoggerFactory();

    public static void SetLoggerFactory(ILoggerFactory loggerFactory)
    {
        s_loggerFactory = loggerFactory;
    }

    internal static ILogger<T> GetLogger<T>() where T : class => s_loggerFactory.CreateLogger<T>();
    internal static ILogger GetLogger(string name) => s_loggerFactory.CreateLogger(name);
}
