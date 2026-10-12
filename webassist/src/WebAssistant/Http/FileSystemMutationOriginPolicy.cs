using WebAssistant.Runtime;

namespace WebAssistant.Http;

internal static class FileSystemMutationOriginPolicy
{
    internal static ValueTask<object?> EnforceAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var options = context.HttpContext.RequestServices
            .GetRequiredService<WebAssistantRuntimeOptions>();
        if (IsAllowed(request, options))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Запрещён источник файловой операции",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "filesystem_origin_forbidden"
            }));
    }

    private static bool IsAllowed(HttpRequest request, WebAssistantRuntimeOptions options)
    {
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || string.IsNullOrEmpty(origins[0]))
            {
                return false;
            }

            var origin = origins[0]!;
            var serviceOrigin = $"http://127.0.0.1:{options.Port}";
            var localhostOrigin = $"http://localhost:{options.Port}";
            return string.Equals(origin, serviceOrigin, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(origin, localhostOrigin, StringComparison.OrdinalIgnoreCase) ||
                   (options.CorsEnabled && options.AllowedOrigins.Contains(origin));
        }

        // При отсутствии Origin нельзя принимать браузерные запросы с иного сайта.
        // Клиенты HTTP без браузерных заголовков сохраняют совместимость.
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSites))
        {
            return fetchSites.Count == 1 &&
                   (string.Equals(fetchSites[0], "same-origin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fetchSites[0], "none", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
