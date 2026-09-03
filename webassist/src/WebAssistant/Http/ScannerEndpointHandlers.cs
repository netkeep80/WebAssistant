using WebAssistant.Scanning;

namespace WebAssistant.Http;

internal static class ScannerEndpointHandlers
{
    internal static async Task<IResult> ListAsync(
        IScanAdapter? adapter,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (adapter is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Модуль сканирования недоступен");
        }

        try
        {
            var scanners = await adapter.GetScannersAsync(cancellationToken);
            logger.LogInformation("Обнаружено сканеров: {ScannerCount}", scanners.Count);
            return Results.Ok(scanners.Select(scanner => new { id = scanner.Id, name = scanner.Name }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Не удалось получить список сканеров");
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Ошибка обнаружения сканеров");
        }
    }
}
