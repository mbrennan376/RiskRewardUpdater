using Microsoft.Extensions.Logging;

namespace RiskReward.PriceService;

public sealed class DailyFileLoggerProvider(string logFolder) : ILoggerProvider
{
    private readonly object gate = new();

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(categoryName, logFolder, gate);
    public void Dispose() { }

    private sealed class DailyFileLogger(string category, string folder, object gate) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var timestamp = DateTimeOffset.Now;
            var line = $"{timestamp:yyyy-MM-dd HH:mm:ss zzz} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            lock (gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"price-service-{timestamp:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
