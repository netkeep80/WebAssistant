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

    public async Task<IReadOnlyList<ScannerDevice>> GetScannersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = await controller.GetDeviceList(Driver.Wia);
        cancellationToken.ThrowIfCancellationRequested();
        var driver = SelectPreferredDriver(devices.Count);
        if (driver == Driver.Twain)
        {
            devices = await controller.GetDeviceList(Driver.Twain);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return devices.Select(device => new ScannerDevice(device.ID, device.Name)).ToArray();
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

        var devices = await controller.GetDeviceList(Driver.Wia);
        cancellationToken.ThrowIfCancellationRequested();
        var driver = SelectPreferredDriver(devices.Count);
        if (driver == Driver.Twain)
        {
            devices = await controller.GetDeviceList(Driver.Twain);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var device = devices.SingleOrDefault(candidate =>
            string.Equals(candidate.ID, scannerId, StringComparison.Ordinal));
        if (device is null)
        {
            throw new InvalidOperationException($"Сканер с идентификатором '{scannerId}' не найден.");
        }

        var options = new ScanOptions
        {
            Driver = driver,
            Device = device,
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

    private static PaperSource MapPaperSource(ScanSource source) => source switch
    {
        ScanSource.Glass => PaperSource.Flatbed,
        ScanSource.Feeder => PaperSource.Feeder,
        ScanSource.Duplex => PaperSource.Duplex,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    private static Driver SelectPreferredDriver(int wiaDeviceCount) =>
        wiaDeviceCount > 0 ? Driver.Wia : Driver.Twain;

    public void Dispose() => scanningContext.Dispose();
}

#pragma warning restore CA2252
