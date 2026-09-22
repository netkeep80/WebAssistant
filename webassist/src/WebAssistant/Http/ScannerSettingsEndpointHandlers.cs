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
        if (!ScannerIdentity.TryParse(scannerId, out _))
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
            var scanner = await adapter.GetScannerCapabilitiesAsync(scannerId, cancellationToken);
            if (scanner is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Сканер не найден");
            }

            var modes = scanner.CapabilityState == ScannerCapabilityState.Unavailable
                ? []
                : ScannerCapabilityProjection.BuildModes(scanner);
            return Results.Ok(new
            {
                scannerId = scanner.Id,
                capabilityState = CapabilityStateName(scanner.CapabilityState),
                modes = modes.Select(ModeResponse).ToArray()
            });
        }
        catch (ScannerOperationTimeoutException exception)
        {
            logger.LogWarning(
                "Превышен deadline scanner operation operation={Operation} backend={Backend} scannerId={ScannerId} timeoutMs={TimeoutMs}",
                OperationName(exception.Operation),
                exception.Backend,
                scannerId,
                (long)exception.Timeout.TotalMilliseconds);
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Превышено время ожидания операции со сканером",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "scanner_operation_timeout",
                    ["operation"] = OperationName(exception.Operation)
                });
        }
        catch (ScannerWorkerRecoveryException exception)
        {
            logger.LogError(
                "Не удалось восстановить scanner worker operation={Operation} backend={Backend} scannerId={ScannerId} workerPid={WorkerPid}",
                OperationName(exception.Operation),
                exception.Backend,
                scannerId,
                exception.WorkerProcessId);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Не удалось восстановить scanner worker",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "scanner_worker_recovery_failed",
                    ["operation"] = OperationName(exception.Operation)
                });
        }

        catch (ScannerBackendUnavailableException exception)
        {
            var diagnosticException = exception.InnerException ?? exception;
            logger.LogWarning(
                "Backend выбранного сканера недоступен backend={Backend} scannerId={ScannerId} exceptionType={ExceptionType} hresult={HResult}",
                exception.Backend,
                scannerId,
                diagnosticException.GetType().Name,
                FormatHResult(diagnosticException));
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Backend сканера недоступен");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Не удалось получить capabilities выбранного сканера scannerId={ScannerId} exceptionType={ExceptionType} hresult={HResult}",
                scannerId,
                exception.GetType().Name,
                FormatHResult(exception));
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Ошибка получения настроек сканера");
        }
    }

    private static string OperationName(ScannerOperationKind operation) => operation switch
    {
        ScannerOperationKind.Discovery => "discovery",
        ScannerOperationKind.Capabilities => "capabilities",
        ScannerOperationKind.Acquisition => "acquisition",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static string CapabilityStateName(ScannerCapabilityState state) => state switch
    {
        ScannerCapabilityState.Unavailable => "unavailable",
        ScannerCapabilityState.Partial => "partial",
        ScannerCapabilityState.Complete => "complete",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

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

    private static string FormatHResult(Exception exception) =>
        $"0x{unchecked((uint)exception.HResult):X8}";
}
