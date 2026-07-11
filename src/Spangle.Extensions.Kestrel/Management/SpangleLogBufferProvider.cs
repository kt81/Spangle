namespace Spangle.Extensions.Kestrel.Management;

/// <summary>
/// Routes every host log record into the <see cref="SpangleLogBuffer"/> so the
/// management log endpoints (and the console log viewer) can serve them.
/// Standard provider-level filtering applies ("SpangleBuffer" alias).
/// </summary>
[ProviderAlias("SpangleBuffer")]
public sealed class SpangleLogBufferProvider(SpangleLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);

    public void Dispose()
    {
    }

    private sealed class BufferLogger(SpangleLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, logLevel, category,
                formatter(state, exception), exception?.ToString()));
        }
    }
}
