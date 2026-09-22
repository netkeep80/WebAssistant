using Microsoft.Extensions.Logging;

namespace WebAssistant.Logging;

internal sealed class ResilientLogger(
    ILogger primary,
    ILogger fallback) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        try
        {
            return primary.BeginScope(state);
        }
        catch (Exception exception)
        {
            WriteSinkFailure(exception);
            return null;
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        try
        {
            return primary.IsEnabled(logLevel);
        }
        catch (Exception exception)
        {
            WriteSinkFailure(exception);
            return fallback.IsEnabled(logLevel);
        }
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            primary.Log(logLevel, eventId, state, exception, formatter);
        }
        catch (Exception sinkException)
        {
            WriteSinkFailure(sinkException);
        }
    }

    private void WriteSinkFailure(Exception exception)
    {
        var diagnostic = UnwrapSingleAggregate(exception);
        fallback.LogWarning(
            "logging.sink.failure exceptionType={ExceptionType} hresult={HResult}",
            diagnostic.GetType().Name,
            $"0x{unchecked((uint)diagnostic.HResult):X8}");
    }

    private static Exception UnwrapSingleAggregate(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;
}
