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
            var discovery = await adapter.GetScannersAsync(cancellationToken);
            logger.LogInformation(
                "Обнаружено сканеров: {ScannerCount}; предупреждений: {WarningCount}",
                discovery.Count,
                discovery.Warnings.Count);

            if (!discovery.IsAvailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Обнаружение сканеров недоступно");
            }

            return Results.Ok(new
            {
                scanners = discovery.Scanners.Select(scanner => new
                {
                    scannerId = scanner.Id,
                    name = scanner.Name,
                    backend = BackendName(scanner.Backend),
                    sources = new
                    {
                        flatbed = scanner.SupportsFlatbed,
                        feeder = scanner.SupportsFeeder,
                        duplex = scanner.SupportsDuplex
                    }
                }),
                warnings = discovery.Warnings.Select(warning => new
                {
                    backend = BackendName(warning.Backend),
                    code = warning.Code
                })
            });
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

    private static string BackendName(ScannerBackend backend) => backend switch
    {
        ScannerBackend.Wia => "wia",
        ScannerBackend.Twain => "twain",
        ScannerBackend.Sane => "sane",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };
}
