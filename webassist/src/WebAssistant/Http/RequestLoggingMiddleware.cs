using System.Diagnostics;

namespace WebAssistant.Http;

internal sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ApiVersion.CurrentPrefix))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var scannerId = TryGetSafeScannerId(context);
        var requestCompleted = false;

        try
        {
            await next(context);
            requestCompleted = true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (scannerId is null)
            {
                logger.LogInformation(
                    "HTTP-запрос отменён клиентом {Method} {Path} elapsedMs={ElapsedMs:F1}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    elapsed.TotalMilliseconds);
            }
            else
            {
                logger.LogInformation(
                    "HTTP-запрос отменён клиентом {Method} {Path} scannerId={ScannerId} elapsedMs={ElapsedMs:F1}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    scannerId,
                    elapsed.TotalMilliseconds);
            }

            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Ошибка HTTP-запроса {Method} {Path} scannerId={ScannerId}",
                context.Request.Method,
                context.Request.Path.Value,
                scannerId);
            throw;
        }
        finally
        {
            if (requestCompleted)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);

                if (scannerId is null)
                {
                    logger.LogInformation(
                        "{Method} {Path} status={StatusCode} elapsedMs={ElapsedMs:F1}",
                        context.Request.Method,
                        context.Request.Path.Value,
                        context.Response.StatusCode,
                        elapsed.TotalMilliseconds);
                }
                else
                {
                    logger.LogInformation(
                        "{Method} {Path} scannerId={ScannerId} status={StatusCode} elapsedMs={ElapsedMs:F1}",
                        context.Request.Method,
                        context.Request.Path.Value,
                        scannerId,
                        context.Response.StatusCode,
                        elapsed.TotalMilliseconds);
                }
            }
        }
    }

    private static string? TryGetSafeScannerId(HttpContext context)
    {
        if (!string.Equals(
                context.Request.Path.Value,
                $"{ApiVersion.CurrentPrefix}/scan",
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!context.Request.Query.TryGetValue("scannerId", out var values))
        {
            return null;
        }

        return Sanitize(values.ToString());
    }

    private static string Sanitize(string value)
    {
        var normalized = new string(
            value
                .Where(character => !char.IsControl(character))
                .Take(200)
                .ToArray());

        return normalized.Length == 0 ? "<empty>" : normalized;
    }
}
