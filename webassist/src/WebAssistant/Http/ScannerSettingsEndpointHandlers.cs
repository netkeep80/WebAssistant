using WebAssistant.Scanning;

namespace WebAssistant.Http;

internal static class ScannerSettingsEndpointHandlers
{
    private const string SchemaFileName = "scanner-settings.schema.json";

    internal static IResult Schema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SchemaFileName);
        return File.Exists(path)
            ? Results.File(path, "application/json")
            : Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Canonical scanner settings schema недоступна");
    }

    internal static async Task<IResult> GetAsync(
        IScanAdapter? adapter,
        string scannerId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!ScannerIdentity.TryParse(scannerId, out var requestedBackend))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный scannerId");
        }

        if (adapter is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Модуль сканирования недоступен");
        }

        try
        {
            var discovery = await adapter.GetScannersAsync(cancellationToken);
            if (!discovery.IsAvailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Обнаружение сканеров недоступно");
            }

            var scanner = discovery.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, scannerId, StringComparison.Ordinal));

            if (scanner is null)
            {
                var backendUnavailable = discovery.Warnings.Any(warning =>
                    warning.Backend == requestedBackend &&
                    string.Equals(warning.Code, "enumerationFailed", StringComparison.Ordinal));

                return backendUnavailable
                    ? Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Backend сканера недоступен")
                    : Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Сканер не найден");
            }

            var modes = ScannerCapabilityProjection.BuildModes(scanner);
            return Results.Ok(new
            {
                scannerId = scanner.Id,
                modes = modes.Select(ModeResponse).ToArray()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Не удалось получить настройки сканера");
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Ошибка получения настроек сканера");
        }
    }

    private static object ModeResponse(ScannerModeCapabilities mode)
    {
        var defaultColor = ScannerCapabilityProjection.DefaultColorMode(mode.Settings);
        var defaultPaperSize = ScannerCapabilityProjection.DefaultPaperSize(mode.Settings);

        return new
        {
            mode = ScannerCapabilityProjection.PublicModeName(mode.Mode),
            source = mode.Source,
            duplex = mode.Duplex,
            settings = new
            {
                dpi = new
                {
                    supported = mode.Settings.DpiValues.Count > 0,
                    values = mode.Settings.DpiValues,
                    unit = "dpi",
                    @default = ScannerCapabilityProjection.DefaultDpi(mode.Settings)
                },
                colorMode = new
                {
                    supported = mode.Settings.ColorModes.Count > 0,
                    values = mode.Settings.ColorModes.Select(ScannerSettingNames.PublicName).ToArray(),
                    @default = defaultColor.HasValue
                        ? ScannerSettingNames.PublicName(defaultColor.Value)
                        : null
                },
                paperSize = new
                {
                    supported = mode.Settings.PaperSizes.Count > 0,
                    values = mode.Settings.PaperSizes.Select(ScannerSettingNames.PublicName).ToArray(),
                    @default = defaultPaperSize.HasValue
                        ? ScannerSettingNames.PublicName(defaultPaperSize.Value)
                        : null
                }
            }
        };
    }
}
