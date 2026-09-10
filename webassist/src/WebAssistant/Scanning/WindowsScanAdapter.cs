using System.Text;
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

    internal WindowsScanAdapter()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            throw new PlatformNotSupportedException("Windows scanner adapter доступен только на Windows.");
        }

        scanningContext = new ScanningContext(new GdiImageContext());
        scanningContext.SetUpWin32Worker();
        controller = new ScanController(scanningContext);
    }

    public Task<ScannerDiscoveryResult> GetScannersAsync(
        CancellationToken cancellationToken = default) =>
        DiscoverAsync(
            driver => controller.GetDeviceList(driver),
            (device, token) => controller.GetCaps(device, token),
            cancellationToken);

    internal static async Task<ScannerDiscoveryResult> DiscoverAsync(
        Func<Driver, Task<List<ScanDevice>>> getDevices,
        Func<ScanDevice, CancellationToken, Task<ScanCaps>> getCaps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getDevices);
        ArgumentNullException.ThrowIfNull(getCaps);

        var scanners = new List<ScannerDevice>();
        var warnings = new List<ScannerDiscoveryWarning>();
        var successfulBackends = 0;

        foreach (var driver in new[] { Driver.Wia, Driver.Twain })
        {
            var backend = MapBackend(driver);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var devices = await getDevices(driver);
                cancellationToken.ThrowIfCancellationRequested();

                var duplicateIds = devices
                    .GroupBy(device => device.ID, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.Ordinal);

                var backendWarnings = new List<ScannerDiscoveryWarning>();
                if (duplicateIds.Count > 0)
                {
                    backendWarnings.Add(new ScannerDiscoveryWarning(
                        backend,
                        "ambiguousNativeIdentity"));
                }

                var normalized = new List<ScannerDevice>();
                foreach (var device in devices)
                {
                    if (duplicateIds.Contains(device.ID))
                    {
                        continue;
                    }

                    var caps = await getCaps(device, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var paperSourceCaps = caps.PaperSourceCaps;

                    normalized.Add(new ScannerDevice(
                        ScannerIdentity.Create(backend, device.ID),
                        device.Name,
                        backend,
                        paperSourceCaps?.SupportsFlatbed ?? false,
                        paperSourceCaps?.SupportsFeeder ?? false,
                        paperSourceCaps?.SupportsDuplex ?? false,
                        MapFeederPaperState(paperSourceCaps?.FeederHasPaper)));
                }

                scanners.AddRange(normalized);
                warnings.AddRange(backendWarnings);
                successfulBackends++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                warnings.Add(new ScannerDiscoveryWarning(backend, "enumerationFailed"));
            }
        }

        return new ScannerDiscoveryResult(
            scanners,
            warnings,
            isAvailable: successfulBackends > 0);
    }

    public Task<Stream> ScanAsync(string scannerId, CancellationToken cancellationToken = default) =>
        ScanAsync(scannerId, ScanSource.Glass, cancellationToken);

    public async Task<Stream> ScanAsync(
        string scannerId,
        ScanSource source,
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
        var devices = await controller.GetDeviceList(driver);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = devices
            .Where(candidate => string.Equals(
                ScannerIdentity.Create(backend, candidate.ID),
                scannerId,
                StringComparison.Ordinal))
            .ToArray();

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

        var images = new List<ProcessedImage>();
        try
        {
            await foreach (var image in controller.Scan(options).WithCancellation(cancellationToken))
            {
                images.Add(image);
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
                if (!await exporter.Export(pdf, images))
                {
                    throw new InvalidOperationException("Не удалось сформировать PDF из отсканированных страниц.");
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

    public void Dispose() => scanningContext.Dispose();
}

#pragma warning restore CA2252
