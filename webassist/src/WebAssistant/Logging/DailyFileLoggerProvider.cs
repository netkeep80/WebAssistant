using System.Text;
using Microsoft.Extensions.Logging;

namespace WebAssistant.Logging;

internal sealed class DailyFileLoggerProvider(string logDirectory) :
    ILoggerProvider,
    ISupportExternalScope
{
    private readonly object writeGate = new();
    private readonly string logDirectory = logDirectory;
    private readonly Encoding encoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) =>
        new DailyFileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        this.scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
    }

    private IDisposable BeginScope<TState>(TState state)
        where TState : notnull =>
        scopeProvider.Push(state);

    private void Write(
        string category,
        LogLevel logLevel,
        string message,
        Exception? exception)
    {
        if (!category.StartsWith("WebAssistant", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var file = Path.Combine(
                logDirectory,
                $"webassistant-{now:yyyy-MM-dd}.log");
            var operationId = ResolveOperationId();

            var text = new StringBuilder()
                .Append(now.ToString("O"))
                .Append(" [")
                .Append(logLevel)
                .Append("] ")
                .Append(category);

            if (operationId is not null)
            {
                text
                    .Append(" operationId=")
                    .Append(operationId);
            }

            text
                .Append(' ')
                .AppendLine(message);

            if (exception is not null)
            {
                text
                    .Append("exceptionType=")
                    .Append(exception.GetType().Name)
                    .Append(" hresult=")
                    .AppendLine(
                        $"0x{unchecked((uint)exception.HResult):X8}");
            }

            lock (writeGate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(file, text.ToString(), encoding);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private string? ResolveOperationId()
    {
        var holder = new ScopeValueHolder();
        scopeProvider.ForEachScope(
            static (scope, state) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                foreach (var value in values)
                {
                    if (!string.Equals(
                            value.Key,
                            "operationId",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    state.Value = SanitizeOperationId(value.Value?.ToString());
                }
            },
            holder);

        return holder.Value;
    }

    private static string? SanitizeOperationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var safe = new string(
            value
                .Where(character =>
                    char.IsLetterOrDigit(character) ||
                    character is '-' or '_' or '.' or ':')
                .Take(96)
                .ToArray());

        return safe.Length == 0 ? null : safe;
    }

    private sealed class DailyFileLogger(
        DailyFileLoggerProvider provider,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            provider.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(
                    categoryName,
                    logLevel,
                    formatter(state, exception),
                    exception);
            }
        }
    }

    private sealed class ScopeValueHolder
    {
        internal string? Value { get; set; }
    }
}
