using NAPS2.Scan.Exceptions;
using WebAssistant.Scanning;

#pragma warning disable CA2252

namespace WebAssistant.Http;

internal sealed class ScanCoordinator(ILogger<ScanCoordinator> logger)
{
    private readonly SemaphoreSlim acquisitionGate = new(1, 1);
    private int busy;

    internal bool IsBusy => Volatile.Read(ref busy) == 1;

    internal async Task<IResult> ExecuteAsync(
        IScanAdapter? adapter,
        ScanRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ScannerId))
        {
            logger.LogWarning("Не указан scannerId");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Не указан scannerId");
        }

        var scannerId = request.ScannerId;
        if (!ScannerIdentity.TryParse(scannerId, out var requestedBackend))
        {
            logger.LogWarning("Получен некорректный scannerId: {ScannerId}", SafeLogText(scannerId));
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный scannerId");
        }

        if (!TryParseRequestedSource(request.Source, out var requestedSource))
        {
            logger.LogWarning("Получен некорректный source");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный source");
        }

        var duplex = request.Settings?.Duplex ?? false;
        if (duplex && requestedSource is not RequestedScanSource.Feeder)
        {
            logger.LogWarning("Двустороннее сканирование запрошено не для source=feeder");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректное сочетание source и duplex");
        }

        if (!TryParseRequestedSettings(request.Settings, out var requestedSettings))
        {
            logger.LogWarning("Получены некорректные scanner settings");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректные параметры сканирования");
        }

        if (adapter is null)
        {
            logger.LogError("Модуль сканирования недоступен для текущей платформы");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Модуль сканирования недоступен");
        }

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
            var discovery = await adapter.GetScannersAsync(cancellationToken);
            logger.LogInformation("Обнаружено сканеров: {ScannerCount}", discovery.Count);

            if (!discovery.IsAvailable)
            {
                logger.LogWarning("Обнаружение сканеров недоступно");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Обнаружение сканеров недоступно");
            }

            selected = discovery.FirstOrDefault(device =>
                string.Equals(device.Id, scannerId, StringComparison.Ordinal));

            if (selected is null)
            {
                var backendUnavailable = discovery.Warnings.Any(warning =>
                    warning.Backend == requestedBackend &&
                    string.Equals(warning.Code, "enumerationFailed", StringComparison.Ordinal));

                logger.LogWarning(
                    backendUnavailable
                        ? "Backend запрошенного scannerId недоступен: {ScannerId}"
                        : "Запрошенный scannerId не найден: {ScannerId}",
                    SafeLogText(scannerId));

                return backendUnavailable
                    ? Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Backend сканера недоступен")
                    : Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Сканер не найден");
            }

            ScanSource source;
            try
            {
                source = ScanSourcePolicy.Resolve(
                    requestedSource,
                    duplex,
                    selected.SupportsFlatbed,
                    selected.SupportsFeeder,
                    selected.SupportsDuplex,
                    selected.FeederPaperState);
            }
            catch (ArgumentException exception)
            {
                logger.LogWarning(exception, "Некорректный запрос источника сканирования");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректные параметры сканирования");
            }
            catch (NotSupportedException exception)
            {
                logger.LogWarning(exception, "Запрошенный режим сканирования не поддерживается");
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Режим сканирования не поддерживается");
            }

            ScannerEffectiveSettings effectiveSettings;
            try
            {
                var requestMode = ScannerCapabilityProjection.ResolveMode(requestedSource, duplex);
                effectiveSettings = ScannerCapabilityProjection.ResolveEffectiveSettings(
                    selected,
                    requestMode,
                    requestedSettings);
            }
            catch (ArgumentException exception)
            {
                logger.LogWarning(exception, "Некорректные normalized scanner settings");
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректные параметры сканирования");
            }
            catch (NotSupportedException exception)
            {
                logger.LogWarning(exception, "Scanner settings не поддерживаются выбранным режимом");
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Настройки сканирования не поддерживаются");
            }

            var safeScannerId = SafeLogText(selected.Id);
            var safeScannerName = SafeLogText(selected.Name);
            logger.LogInformation(
                "Начало сканирования scannerId={ScannerId} scannerName={ScannerName} source={ScanSource}",
                safeScannerId,
                safeScannerName,
                source);

            Stream pdf;
            try
            {
                pdf = await adapter.ScanAsync(
                    selected.Id,
                    source,
                    effectiveSettings,
                    cancellationToken);
            }
            catch (DeviceFeederEmptyException) when (
                requestedSource == RequestedScanSource.Auto &&
                source == ScanSource.Feeder &&
                selected.SupportsFlatbed)
            {
                logger.LogInformation(
                    "Автовыбор feeder оказался пустым; повторное сканирование со стекла scannerId={ScannerId} scannerName={ScannerName}",
                    safeScannerId,
                    safeScannerName);
                pdf = await adapter.ScanAsync(
                    selected.Id,
                    ScanSource.Glass,
                    effectiveSettings,
                    cancellationToken);
            }

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
                selected is null ? SafeLogText(scannerId) : SafeLogText(selected.Id));
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

    private static bool TryParseRequestedSource(
        string? value,
        out RequestedScanSource source)
    {
        switch (value)
        {
            case null:
            case "auto":
                source = RequestedScanSource.Auto;
                return true;
            case "flatbed":
                source = RequestedScanSource.Flatbed;
                return true;
            case "feeder":
                source = RequestedScanSource.Feeder;
                return true;
            default:
                source = default;
                return false;
        }
    }

    private static bool TryParseRequestedSettings(
        ScanSettings? settings,
        out ScannerRequestedSettings requested)
    {
        if (settings?.Dpi is int dpi && dpi <= 0)
        {
            requested = default;
            return false;
        }

        if (!ScannerSettingNames.TryParseColorMode(settings?.ColorMode, out var colorMode) ||
            !ScannerSettingNames.TryParsePaperSize(settings?.PaperSize, out var paperSize))
        {
            requested = default;
            return false;
        }

        requested = new ScannerRequestedSettings(settings?.Dpi, colorMode, paperSize);
        return true;
    }

    private static string SafeLogText(string value)
    {
        return new string(
            value.Where(character => !char.IsControl(character)).Take(200).ToArray());
    }
}

#pragma warning restore CA2252
