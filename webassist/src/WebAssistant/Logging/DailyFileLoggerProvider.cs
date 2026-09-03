using System.Text;
using Microsoft.Extensions.Logging;

namespace WebAssistant.Logging;

internal sealed class DailyFileLoggerProvider(string logDirectory) : ILoggerProvider
{
    private readonly object writeGate = new();
    private readonly string logDirectory = logDirectory;
    private readonly Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel logLevel, string message, Exception? exception)
    {
        if (!category.StartsWith("WebAssistant", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var file = Path.Combine(logDirectory, $"webassistant-{now:yyyy-MM-dd}.log");
            var text = new StringBuilder()
                .Append(now.ToString("O"))
                .Append(" [")
                .Append(logLevel)
                .Append("] ")
                .Append(category)
                .Append(' ')
                .AppendLine(message);

            if (exception is not null)
            {
                text.AppendLine(exception.ToString());
            }

            lock (writeGate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(file, text.ToString(), encoding);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (NotSupportedException) { }
    }

    private sealed class DailyFileLogger(DailyFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(categoryName, logLevel, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
