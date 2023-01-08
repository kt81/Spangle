using Cysharp.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Spangle.Examples.Console;

public static class LoggerExtensions
{
    public static void AddColorizedZLoggerConsole(this ILoggingBuilder builder, string concernedTarget)
    {
        builder.AddZLoggerConsole(configureEnableAnsiEscapeCode: true, configure: options =>
        {
            options.PrefixFormatter = (writer, info) =>
            {
                if (info.LogLevel == LogLevel.Error)
                {
                    ZString.Utf8Format(writer, "\u001b[31m[{0}][{1}] ", info.LogLevel, info.CategoryName);
                }
                else
                {
                    if (!info.CategoryName.StartsWith(concernedTarget))
                    {
                        ZString.Utf8Format(writer, "\u001b[38;5;08m[{0}][{1}] ", info.LogLevel, info.CategoryName);
                    }
                    else
                    {
                        ZString.Utf8Format(writer, "[{0}][{1}] ", info.LogLevel, info.CategoryName);
                    }
                }
            };
            options.SuffixFormatter = (writer, info) =>
            {
                if (info.LogLevel == LogLevel.Error || !info.CategoryName.StartsWith(concernedTarget))
                {
                    ZString.Utf8Format(writer, "\u001b[0m", "");
                }
            };
        });
    }
}
