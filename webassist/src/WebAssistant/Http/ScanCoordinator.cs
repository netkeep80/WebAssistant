using WebAssistant.Scanning;

namespace WebAssistant.Http;

internal sealed class ScanCoordinator(ILogger<ScanCoordinator> logger)
{
    private readonly SemaphoreSlim acquisitionGate = new(1, 1);
    private int busy;

    internal bool IsBusy => Volatile.Read(ref busy) == 1;

    internal async Task<IResult> ExecuteAsync(
        IScanAdapter? adapter,
        string? scannerId,
        ScanSource source,
        CancellationToken cancellationToken)
    {
        if (!await acquisitionGate.WaitAsync(0, cancellationToken))
        {
            logger.LogWarning("Операция сканирования отклонена: scanner resource занят");
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Сканер занят",
                detail: "Другая операция сканирования уже выполняется.");
        }

        Volatile.Write(ref busy, 1);
        ScannerDevice? selected = null;

        try
        {
            if (adapter is null)
            {
                logger.LogError("Модуль сканирования недоступен для текущей платформы");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Модуль сканирования недоступен");
            }

            if (scannerId is not null && string.IsNullOrWhiteSpace(scannerId))
            {
                logger.LogWarning("Получен пустой scannerId");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Не указан scannerId");
            }

            var scanners = await adapter.GetScannersAsync(cancellationToken);
            logger.LogInformation("Обнаружено сканеров: {ScannerCount}", scanners.Count);

            if (scannerId is not null)
            {
                selected = scanners.FirstOrDefault(device =>
                    string.Equals(device.Id, scannerId, StringComparison.Ordinal));

                if (selected is null)
                {
                    logger.LogWarning(
                        "Запрошенный scannerId не найден: {ScannerId}",
                        SafeLogText(scannerId));
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Сканер не найден");
                }
            }
            else
            {
                if (scanners.Count == 0)
                {
                    logger.LogWarning("Сканеры не найдены");
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Сканеры не найдены");
                }

                if (scanners.Count > 1)
                {
                    logger.LogWarning(
                        "Автоматический выбор запрещён: обнаружено сканеров {ScannerCount}",
                        scanners.Count);
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Необходим выбор сканера",
                        detail: "Обнаружено несколько сканеров; автоматический выбор запрещён.");
                }

                selected = scanners[0];
            }

            var safeScannerId = SafeLogText(selected.Id);
            var safeScannerName = SafeLogText(selected.Name);
            logger.LogInformation(
                "Начало сканирования scannerId={ScannerId} scannerName={ScannerName} source={ScanSource}",
                safeScannerId,
                safeScannerName,
                source);

            var pdf = await adapter.ScanAsync(selected.Id, source, cancellationToken);

            if (!pdf.CanRead || (pdf.CanSeek && pdf.Length == 0))
            {
                await pdf.DisposeAsync();
                logger.LogError("Сканер не вернул PDF scannerId={ScannerId}", safeScannerId);
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Сканер не вернул PDF");
            }

            logger.LogInformation("Сканирование успешно завершено scannerId={ScannerId}", safeScannerId);
            return Results.Stream(pdf, contentType: "application/pdf");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Сканирование отменено scannerId={ScannerId}",
                selected is null ? null : SafeLogText(selected.Id));
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Ошибка сканирования scannerId={ScannerId}",
                selected is null ? null : SafeLogText(selected.Id));
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Ошибка сканирования");
        }
        finally
        {
            Volatile.Write(ref busy, 0);
            acquisitionGate.Release();
        }
    }

    private static string SafeLogText(string value)
    {
        return new string(
            value.Where(character => !char.IsControl(character)).Take(200).ToArray());
    }
}
