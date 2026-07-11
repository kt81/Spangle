using Microsoft.Extensions.Logging;
using ZLogger;

namespace Spangle.Examples.Console;

internal static class LoggerExtensions
{
    public static void AddColorizedZLoggerConsole(this ILoggingBuilder builder, string concernedTarget)
    {
        builder.AddZLoggerConsole(options =>
        {
            options.UsePlainTextFormatter(formatter =>
            {
                // \u001b[31m => Red(ANSI Escape Code)
                // \u001b[0m => Reset
                // \u001b[38;5;***m => 256 Colors(08 is Gray)
                formatter.SetPrefixFormatter($"{0}{1}|{2:short}|", (in MessageTemplate writer, in LogInfo info) =>
                {
                    var escapeSequence = "";
                    if (info.LogLevel >= LogLevel.Error)
                    {
                        escapeSequence = "\u001b[31m";
                    }
                    else if (!info.Category.Name.Contains(concernedTarget, StringComparison.Ordinal))
                    {
                        escapeSequence = "\u001b[38;5;08m";
                    }

                    writer.Format(escapeSequence, info.Timestamp, info.LogLevel);
                });

                formatter.SetSuffixFormatter($"{0}", (in MessageTemplate writer, in LogInfo info) =>
                {
                    if (info.LogLevel == LogLevel.Error || !info.Category.Name.Contains(concernedTarget, StringComparison.Ordinal))
                    {
                        writer.Format("\u001b[0m");
                    }
                });
            });
        });
    }
}
