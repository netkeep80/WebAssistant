using Microsoft.Extensions.Logging;

namespace WebAssistant.Logging;

internal sealed class ResilientLogger(
    ILogger primary,
    ILogger fallback) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        IDisposable? primaryScope = null;
        IDisposable? fallbackScope = null;

        try
        {
            primaryScope = primary.BeginScope(state);
        }
        catch (Exception exception)
        {
            WriteSinkFailure(exception);
        }

        try
        {
            fallbackScope = fallback.BeginScope(state);
        }
        catch
        {
            // Fallback diagnostics must never become a new application failure.
        }

        return primaryScope is null && fallbackScope is null
            ? null
            : new ResilientScope(this, primaryScope, fallbackScope);
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
            try
            {
                return fallback.IsEnabled(logLevel);
            }
            catch
            {
                return false;
            }
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

    private void DisposePrimaryScope(IDisposable? scope)
    {
        if (scope is null)
        {
            return;
        }

        try
        {
            scope.Dispose();
        }
        catch (Exception exception)
        {
            WriteSinkFailure(exception);
        }
    }

    private static void DisposeFallbackScope(IDisposable? scope)
    {
        if (scope is null)
        {
            return;
        }

        try
        {
            scope.Dispose();
        }
        catch
        {
            // Fallback diagnostics must never become a new application failure.
        }
    }

    private void WriteSinkFailure(Exception exception)
    {
        var diagnostic = UnwrapSingleAggregate(exception);
        try
        {
            fallback.LogWarning(
                "logging.sink.failure exceptionType={ExceptionType} hresult={HResult}",
                diagnostic.GetType().Name,
                $"0x{unchecked((uint)diagnostic.HResult):X8}");
        }
        catch
        {
            // Logging is a secondary concern and must never mask the primary failure.
        }
    }

    private static Exception UnwrapSingleAggregate(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;

    private sealed class ResilientScope(
        ResilientLogger owner,
        IDisposable? primaryScope,
        IDisposable? fallbackScope) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            owner.DisposePrimaryScope(primaryScope);
            DisposeFallbackScope(fallbackScope);
        }
    }
}
