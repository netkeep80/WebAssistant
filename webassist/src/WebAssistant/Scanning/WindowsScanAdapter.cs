using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Pdf;
using NAPS2.Scan;

#pragma warning disable CA2252

namespace WebAssistant.Scanning;

internal sealed class WindowsScanAdapter : IScanAdapter, IDisposable
{
    private readonly ScanningContext scanningContext;
    private readonly ScanController controller;
    private readonly ILogger? logger;
    private readonly AsyncLocal<ScannerWorkerLeaseCapture?> activeWorkerLeaseCapture = new();
    private int disposed;

    internal WindowsScanAdapter(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            throw new PlatformNotSupportedException("Windows scanner adapter доступен только на Windows.");
        }

        this.logger = logger;
        scanningContext = new ScanningContext(new GdiImageContext());
        if (logger is not null)
        {
            scanningContext.Logger = logger;
        }
        scanningContext.SetUpWin32Worker();
        scanningContext.WorkerLeaseAcquired = lease =>
        {
            var capture = activeWorkerLeaseCapture.Value;
            if (capture is not null)
            {
                capture.Publish(new Naps2WorkerLease(lease));
            }
        };
        controller = new ScanController(scanningContext);
    }

    public Task<ScannerDiscoveryResult> GetScannersAsync(
        CancellationToken cancellationToken = default)
    {
        Func<Driver, Task<List<ScanDevice>>> getDevices =
            driver => GetDeviceListWithBoundaryAsync(driver, cancellationToken);
        return logger is null
            ? DiscoverAsync(getDevices, cancellationToken)
            : DiscoverAsync(getDevices, logger, cancellationToken);
    }

    public Task<ScannerDevice?> GetScannerAsync(
        string scannerId,
        CancellationToken cancellationToken = default)
    {
        Func<Driver, Task<List<ScanDevice>>> getDevices =
            driver => GetDeviceListWithBoundaryAsync(driver, cancellationToken);
        return logger is null
            ? ResolveRegisteredEndpointAsync(scannerId, getDevices, cancellationToken)
            : ResolveRegisteredEndpointAsync(scannerId, getDevices, logger, cancellationToken);
    }

    public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
        string scannerId,
        CancellationToken cancellationToken = default)
    {
        Func<Driver, Task<List<ScanDevice>>> getDevices =
            driver => GetDeviceListWithBoundaryAsync(driver, cancellationToken);
        var isTwain = ScannerIdentity.TryParse(scannerId, out var backend) &&
            backend == ScannerBackend.Twain;
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps =
            (device, token) => isTwain
                ? ExecuteTwainWorkerOperationAsync(
                    ScannerOperationKind.Capabilities,
                    ScannerOperationDeadlines.Capabilities,
                    token,
                    () => controller.GetCaps(device, token))
                : controller.GetCaps(device, token);
        return logger is null
            ? ResolveCapabilitiesAsync(scannerId, getDevices, getCaps, cancellationToken)
            : ResolveCapabilitiesAsync(scannerId, getDevices, getCaps, logger, cancellationToken);
    }

    internal static Task<ScannerDiscoveryResult> DiscoverAsync(
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        CancellationToken cancellationToken = default) =>
        DiscoverCoreAsync(getDevices, logger: null, cancellationToken);

    internal static Task<ScannerDiscoveryResult> DiscoverAsync(
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return DiscoverCoreAsync(getDevices, logger, cancellationToken);
    }

    private static async Task<ScannerDiscoveryResult> DiscoverCoreAsync(
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getDevices);

        var scanners = new List<ScannerDevice>();
        var warnings = new List<ScannerDiscoveryWarning>();
        var successfulBackends = 0;

        foreach (var driver in new[] { Driver.Wia, Driver.Twain })
        {
            var backend = MapBackend(driver);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger?.LogDebug(
                    "scanner.discovery backend={Backend} stage=getDeviceList event=start",
                    BackendName(backend));
                var devices = await getDevices(driver);
                cancellationToken.ThrowIfCancellationRequested();
                stopwatch.Stop();
                successfulBackends++;

                logger?.LogInformation(
                    "scanner.discovery backend={Backend} stage=getDeviceList outcome=success durationMs={DurationMs} deviceCount={DeviceCount}",
                    BackendName(backend),
                    stopwatch.ElapsedMilliseconds,
                    devices.Count);

                var duplicateIds = devices
                    .GroupBy(device => device.ID, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.Ordinal);

                if (duplicateIds.Count > 0)
                {
                    AddWarningOnce(warnings, backend, "ambiguousNativeIdentity");
                }

                foreach (var device in devices)
                {
                    if (duplicateIds.Contains(device.ID))
                    {
                        continue;
                    }

                    scanners.Add(CreateRegisteredEndpoint(device, backend));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                logger?.LogWarning(
                    "scanner.discovery backend={Backend} stage=getDeviceList outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                    BackendName(backend),
                    stopwatch.ElapsedMilliseconds,
                    exception.GetType().Name,
                    FormatHResult(exception));
                AddWarningOnce(warnings, backend, "enumerationFailed");
            }
        }

        return new ScannerDiscoveryResult(
            scanners,
            warnings,
            isAvailable: successfulBackends > 0);
    }

    // Compatibility overload used by regression tests that prove capability probes are not part of listing.
    internal static Task<ScannerDiscoveryResult> DiscoverAsync(
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getCaps);
        return DiscoverAsync(getDevices, cancellationToken);
    }

    internal static Task<ScannerDevice?> ResolveRegisteredEndpointAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        CancellationToken cancellationToken = default) =>
        ResolveRegisteredEndpointCoreAsync(
            scannerId,
            getDevices,
            logger: null,
            cancellationToken);

    internal static Task<ScannerDevice?> ResolveRegisteredEndpointAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return ResolveRegisteredEndpointCoreAsync(
            scannerId,
            getDevices,
            logger,
            cancellationToken);
    }

    private static async Task<ScannerDevice?> ResolveRegisteredEndpointCoreAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        ArgumentNullException.ThrowIfNull(getDevices);

        if (!ScannerIdentity.TryParse(scannerId, out var backend) ||
            backend is not (ScannerBackend.Wia or ScannerBackend.Twain))
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' не принадлежит Windows scanner backend.");
        }

        var devices = await EnumerateSelectedBackendAsync(
            "scanner.resolve",
            scannerId,
            backend,
            MapDriver(backend),
            getDevices,
            logger,
            cancellationToken);

        var matches = FindMatches(devices, backend, scannerId);
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' неоднозначен внутри backend '{backend}'.");
        }

        return CreateRegisteredEndpoint(matches[0], backend);
    }

    internal static Task<ScannerDevice?> ResolveCapabilitiesAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps,
        CancellationToken cancellationToken = default) =>
        ResolveCapabilitiesCoreAsync(
            scannerId,
            getDevices,
            getCaps,
            logger: null,
            cancellationToken);

    internal static Task<ScannerDevice?> ResolveCapabilitiesAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return ResolveCapabilitiesCoreAsync(
            scannerId,
            getDevices,
            getCaps,
            logger,
            cancellationToken);
    }

    private static async Task<ScannerDevice?> ResolveCapabilitiesCoreAsync(
        string scannerId,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        ArgumentNullException.ThrowIfNull(getDevices);
        ArgumentNullException.ThrowIfNull(getCaps);

        if (!ScannerIdentity.TryParse(scannerId, out var backend) ||
            backend is not (ScannerBackend.Wia or ScannerBackend.Twain))
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' не принадлежит Windows scanner backend.");
        }

        var driver = MapDriver(backend);
        var devices = await EnumerateSelectedBackendAsync(
            "scanner.capabilities",
            scannerId,
            backend,
            driver,
            getDevices,
            logger,
            cancellationToken);

        var matches = FindMatches(devices, backend, scannerId);
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' неоднозначен внутри backend '{backend}'.");
        }

        var device = matches[0];
        var stopwatch = Stopwatch.StartNew();
        try
        {
            logger?.LogDebug(
                "scanner.capabilities backend={Backend} scannerId={ScannerId} stage=getCaps event=start",
                BackendName(backend),
                scannerId);
            var backendActive = 1;
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Volatile.Read(ref backendActive) == 1)
                {
                    logger?.LogDebug(
                        "scanner.capabilities backend={Backend} scannerId={ScannerId} stage=getCaps cancellation=backendRequested operationState=active",
                        BackendName(backend),
                        scannerId);
                }
            });

            ScanCaps caps;
            try
            {
                caps = await getCaps(device, cancellationToken);
            }
            finally
            {
                Volatile.Write(ref backendActive, 0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            logger?.LogInformation(
                "scanner.capabilities backend={Backend} scannerId={ScannerId} stage=getCaps outcome=success durationMs={DurationMs}",
                BackendName(backend),
                scannerId,
                stopwatch.ElapsedMilliseconds);
            return CreateCapableEndpoint(device, backend, caps);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger?.LogDebug(
                "scanner.capabilities backend={Backend} scannerId={ScannerId} stage=getCaps outcome=cancelled durationMs={DurationMs}",
                BackendName(backend),
                scannerId,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (ScannerOperationTimeoutException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (ScannerWorkerRecoveryException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger?.LogWarning(
                "scanner.capabilities backend={Backend} scannerId={ScannerId} stage=getCaps outcome=failure durationMs={DurationMs} capabilityState=unavailable exceptionType={ExceptionType} hresult={HResult}",
                BackendName(backend),
                scannerId,
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                FormatHResult(exception));
            return CreateRegisteredEndpoint(device, backend);
        }
    }

    public Task<Stream> ScanAsync(string scannerId, CancellationToken cancellationToken = default) =>
        ScanAsync(scannerId, ScanSource.Glass, ScannerEffectiveSettings.Unspecified, cancellationToken);

    public Task<Stream> ScanAsync(
        string scannerId,
        ScanSource source,
        CancellationToken cancellationToken = default) =>
        ScanAsync(scannerId, source, ScannerEffectiveSettings.Unspecified, cancellationToken);

    public async Task<Stream> ScanAsync(
        string scannerId,
        ScanSource source,
        ScannerEffectiveSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ScannerIdentity.TryParse(scannerId, out var backend) ||
            backend is not (ScannerBackend.Wia or ScannerBackend.Twain))
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' не принадлежит Windows scanner backend.");
        }

        var driver = MapDriver(backend);
        var devices = await EnumerateSelectedBackendAsync(
            "scanner.scan",
            scannerId,
            backend,
            driver,
            selectedDriver => GetDeviceListWithBoundaryAsync(
                selectedDriver,
                cancellationToken),
            logger,
            cancellationToken);
        var matches = FindMatches(devices, backend, scannerId);

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"Сканер с идентификатором '{scannerId}' не найден.");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"ScannerId '{scannerId}' неоднозначен внутри backend '{backend}'.");
        }

        var options = new ScanOptions
        {
            Driver = driver,
            Device = matches[0],
            PaperSource = MapPaperSource(source)
        };
        Naps2ScannerCapabilityMapper.Apply(options, settings);

        var images = new List<ProcessedImage>();
        try
        {
            var acquisitionStopwatch = Stopwatch.StartNew();
            logger?.LogDebug(
                "scanner.scan backend={Backend} scannerId={ScannerId} stage=acquisition event=start source={Source}",
                BackendName(backend),
                scannerId,
                source);
            try
            {
                var acquiredImages = backend == ScannerBackend.Twain
                    ? await ExecuteTwainWorkerOperationAsync(
                        ScannerOperationKind.Acquisition,
                        ScannerOperationDeadlines.Acquisition,
                        cancellationToken,
                        () => AcquireImagesAsync(options, cancellationToken))
                    : await AcquireImagesAsync(options, cancellationToken);
                images.AddRange(acquiredImages);

                acquisitionStopwatch.Stop();
                logger?.LogInformation(
                    "scanner.scan backend={Backend} scannerId={ScannerId} stage=acquisition outcome=success durationMs={DurationMs} pageCount={PageCount}",
                    BackendName(backend),
                    scannerId,
                    acquisitionStopwatch.ElapsedMilliseconds,
                    images.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                acquisitionStopwatch.Stop();
                logger?.LogDebug(
                    "scanner.scan backend={Backend} scannerId={ScannerId} stage=acquisition outcome=cancelled durationMs={DurationMs}",
                    BackendName(backend),
                    scannerId,
                    acquisitionStopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception exception)
            {
                acquisitionStopwatch.Stop();
                logger?.LogWarning(
                    "scanner.scan backend={Backend} scannerId={ScannerId} stage=acquisition outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                    BackendName(backend),
                    scannerId,
                    acquisitionStopwatch.ElapsedMilliseconds,
                    exception.GetType().Name,
                    FormatHResult(exception));
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (images.Count == 0)
            {
                throw new InvalidOperationException("Сканер не вернул ни одной страницы.");
            }

            var pdf = new MemoryStream();
            try
            {
                var exporter = new PdfExporter(scanningContext);
                var exportStopwatch = Stopwatch.StartNew();
                logger?.LogDebug(
                    "scanner.scan backend={Backend} scannerId={ScannerId} stage=pdfExport event=start pageCount={PageCount}",
                    BackendName(backend),
                    scannerId,
                    images.Count);
                try
                {
                    if (!await exporter.Export(pdf, images))
                    {
                        throw new InvalidOperationException("Не удалось сформировать PDF из отсканированных страниц.");
                    }

                    exportStopwatch.Stop();
                    logger?.LogInformation(
                        "scanner.scan backend={Backend} scannerId={ScannerId} stage=pdfExport outcome=success durationMs={DurationMs} pageCount={PageCount} pdfBytes={PdfBytes}",
                        BackendName(backend),
                        scannerId,
                        exportStopwatch.ElapsedMilliseconds,
                        images.Count,
                        pdf.Length);
                }
                catch (Exception exception)
                {
                    exportStopwatch.Stop();
                    logger?.LogWarning(
                        "scanner.scan backend={Backend} scannerId={ScannerId} stage=pdfExport outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                        BackendName(backend),
                        scannerId,
                        exportStopwatch.ElapsedMilliseconds,
                        exception.GetType().Name,
                        FormatHResult(exception));
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (pdf.Length < 5)
                {
                    throw new InvalidOperationException("Полученный PDF слишком короткий.");
                }

                pdf.Position = 0;
                var signatureBytes = new byte[5];
                var bytesRead = await pdf.ReadAsync(signatureBytes.AsMemory(), cancellationToken);
                if (bytesRead != 5 || Encoding.ASCII.GetString(signatureBytes) != "%PDF-")
                {
                    throw new InvalidOperationException("Сканирование не сформировало корректный PDF.");
                }

                pdf.Position = 0;
                return pdf;
            }
            catch
            {
                pdf.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    private Task<List<ScanDevice>> GetDeviceListWithBoundaryAsync(
        Driver driver,
        CancellationToken cancellationToken)
    {
        if (driver != Driver.Twain)
        {
            return controller.GetDeviceList(driver);
        }

        return ExecuteTwainWorkerOperationAsync(
            ScannerOperationKind.Discovery,
            ScannerOperationDeadlines.Discovery,
            cancellationToken,
            () => controller.GetDeviceList(driver));
    }

    private async Task<T> ExecuteTwainWorkerOperationAsync<T>(
        ScannerOperationKind operation,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        Func<Task<T>> operationFactory)
    {
        if (activeWorkerLeaseCapture.Value is not null)
        {
            throw new InvalidOperationException(
                "Вложенная TWAIN worker operation не поддерживается.");
        }

        var capture = new ScannerWorkerLeaseCapture();
        activeWorkerLeaseCapture.Value = capture;
        logger?.LogDebug(
            "scanner.workerBoundary backend=twain operation={Operation} event=start deadlineMs={DeadlineMs}",
            OperationName(operation),
            (long)deadline.TotalMilliseconds);

        try
        {
            return await ScannerWorkerBoundary.ExecuteAsync(
                    operation,
                    ScannerBackend.Twain,
                    deadline,
                    cancellationToken,
                    capture,
                    operationFactory)
                .ConfigureAwait(false);
        }
        catch (ScannerOperationTimeoutException)
        {
            logger?.LogWarning(
                "scanner.workerBoundary backend=twain operation={Operation} outcome=timeout deadlineMs={DeadlineMs}",
                OperationName(operation),
                (long)deadline.TotalMilliseconds);
            throw;
        }
        catch (ScannerWorkerRecoveryException exception)
        {
            logger?.LogError(
                "scanner.workerBoundary backend=twain operation={Operation} outcome=recoveryFailed workerPid={WorkerPid} exceptionType={ExceptionType} hresult={HResult}",
                OperationName(operation),
                exception.WorkerProcessId,
                exception.InnerException?.GetType().Name ?? exception.GetType().Name,
                FormatHResult(exception.InnerException ?? exception));
            throw;
        }
        finally
        {
            activeWorkerLeaseCapture.Value = null;
        }
    }

    private async Task<List<ProcessedImage>> AcquireImagesAsync(
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        var acquired = new List<ProcessedImage>();
        try
        {
            await foreach (var image in controller
                               .Scan(options, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                acquired.Add(image);
            }

            return acquired;
        }
        catch
        {
            foreach (var image in acquired)
            {
                image.Dispose();
            }

            throw;
        }
    }

    private static string OperationName(ScannerOperationKind operation) => operation switch
    {
        ScannerOperationKind.Discovery => "discovery",
        ScannerOperationKind.Capabilities => "capabilities",
        ScannerOperationKind.Acquisition => "acquisition",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private sealed class Naps2WorkerLease(WorkerProcessLease lease) : IScannerWorkerLease
    {
        public int ProcessId => lease.ProcessId;

        public Task TerminateAsync(
            ScannerWorkerTerminationReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return lease.TerminateAsync(reason switch
            {
                ScannerWorkerTerminationReason.Deadline => "deadline",
                ScannerWorkerTerminationReason.ClientCancellation => "clientCancellation",
                _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
            });
        }
    }

    private static ScanDevice[] FindMatches(
        IEnumerable<ScanDevice> devices,
        ScannerBackend backend,
        string scannerId) =>
        devices
            .Where(candidate => string.Equals(
                ScannerIdentity.Create(backend, candidate.ID),
                scannerId,
                StringComparison.Ordinal))
            .ToArray();

    private static async Task<List<ScanDevice>> EnumerateSelectedBackendAsync(
        string operation,
        string scannerId,
        ScannerBackend backend,
        Driver driver,
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger?.LogDebug(
                "{Operation} backend={Backend} scannerId={ScannerId} stage=getDeviceList event=start",
                operation,
                BackendName(backend),
                scannerId);
            var devices = await getDevices(driver);
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            logger?.LogInformation(
                "{Operation} backend={Backend} scannerId={ScannerId} stage=getDeviceList outcome=success durationMs={DurationMs} deviceCount={DeviceCount}",
                operation,
                BackendName(backend),
                scannerId,
                stopwatch.ElapsedMilliseconds,
                devices.Count);
            return devices;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ScannerOperationTimeoutException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (ScannerWorkerRecoveryException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger?.LogWarning(
                "{Operation} backend={Backend} scannerId={ScannerId} stage=getDeviceList outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                operation,
                BackendName(backend),
                scannerId,
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                FormatHResult(exception));
            throw new ScannerBackendUnavailableException(backend, exception);
        }
    }

    private static ScannerDevice CreateRegisteredEndpoint(
        ScanDevice device,
        ScannerBackend backend) =>
        new(
            ScannerIdentity.Create(backend, device.ID),
            device.Name,
            backend,
            SupportsFlatbed: false,
            SupportsFeeder: false,
            SupportsDuplex: false,
            FeederPaperState.Unknown,
            Capabilities: null,
            CapabilityState: ScannerCapabilityState.Unavailable);

    private static ScannerDevice CreateCapableEndpoint(
        ScanDevice device,
        ScannerBackend backend,
        ScanCaps caps)
    {
        var paperSourceCaps = caps.PaperSourceCaps;
        return new ScannerDevice(
            ScannerIdentity.Create(backend, device.ID),
            device.Name,
            backend,
            paperSourceCaps?.SupportsFlatbed ?? false,
            paperSourceCaps?.SupportsFeeder ?? false,
            paperSourceCaps?.SupportsDuplex ?? false,
            MapFeederPaperState(paperSourceCaps?.FeederHasPaper),
            Naps2ScannerCapabilityMapper.From(caps),
            ScannerCapabilityState.Complete);
    }

    private static string FormatHResult(Exception exception) =>
        $"0x{unchecked((uint)exception.HResult):X8}";

    private static string BackendName(ScannerBackend backend) =>
        backend.ToString().ToLowerInvariant();

    private static void AddWarningOnce(
        ICollection<ScannerDiscoveryWarning> warnings,
        ScannerBackend backend,
        string code)
    {
        var warning = new ScannerDiscoveryWarning(backend, code);
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }

    private static ScannerBackend MapBackend(Driver driver) => driver switch
    {
        Driver.Wia => ScannerBackend.Wia,
        Driver.Twain => ScannerBackend.Twain,
        _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
    };

    private static Driver MapDriver(ScannerBackend backend) => backend switch
    {
        ScannerBackend.Wia => Driver.Wia,
        ScannerBackend.Twain => Driver.Twain,
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };

    private static FeederPaperState MapFeederPaperState(bool? feederHasPaper) => feederHasPaper switch
    {
        true => FeederPaperState.Present,
        false => FeederPaperState.Absent,
        null => FeederPaperState.Unknown
    };

    private static PaperSource MapPaperSource(ScanSource source) => source switch
    {
        ScanSource.Glass => PaperSource.Flatbed,
        ScanSource.Feeder => PaperSource.Feeder,
        ScanSource.Duplex => PaperSource.Duplex,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        scanningContext.Dispose();
    }
}

#pragma warning restore CA2252
