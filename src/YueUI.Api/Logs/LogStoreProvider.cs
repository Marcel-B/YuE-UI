namespace YueUI.Api.Logs;

/// <summary>
/// Hands every log message of the server to the <see cref="LogStore"/>, beside the console that launchd writes to
/// <c>yueui.log</c>. The levels in <c>Logging:LogLevel</c> apply to both.
/// </summary>
public sealed class LogStoreProvider(LogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StoreLogger(store, LogSources.ForCategory(categoryName));

    public void Dispose()
    {
    }

    private sealed class StoreLogger(LogStore store, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            store.Add(source, LogLevels.From(logLevel), formatter(state, exception), exception?.ToString());
        }
    }
}
