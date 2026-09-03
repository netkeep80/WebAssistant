using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebAssistant.Http;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class RequestLoggingMiddlewareTests
{
    [Fact]
    public async Task RequestAbortedCancellation_IsNotLoggedAsErrorOrSuccessful200()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            _ => Task.FromCanceled(cancellation.Token),
            logger);
        var context = CreateApiContext();
        context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context));

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Level == LogLevel.Error);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("отмен", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("status=200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedException_IsLoggedAsErrorWithoutSuccessfulCompletion()
    {
        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            _ => Task.FromException(new InvalidOperationException("boom")),
            logger);
        var context = CreateApiContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context));

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error &&
                     entry.Exception is InvalidOperationException);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("status=200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedRequest_LogsActualStatus()
    {
        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return Task.CompletedTask;
            },
            logger);
        var context = CreateApiContext();

        await middleware.InvokeAsync(context);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("status=409", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScannerId_IsSanitizedBeforeLogging()
    {
        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);
        var context = CreateApiContext("scanner\r\nforged-entry");

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain("\r", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry.Message, StringComparison.Ordinal);
        Assert.Contains("scannerforged-entry", entry.Message, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateApiContext(string scannerId = "scanner-1")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/scan";
        context.Request.QueryString = new QueryString(
            $"?scannerId={Uri.EscapeDataString(scannerId)}");
        return context;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return EmptyScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
