using System.Text;
using NAPS2.Images;
using NAPS2.Images.Gtk;
using NAPS2.Pdf;
using NAPS2.Scan;

#pragma warning disable CA2252

namespace WebAssistant.Scanning;

internal sealed class LinuxScanAdapter : IScanAdapter, IDisposable
{
    private readonly ScanningContext scanningContext;
    private readonly ScanController controller;

    internal LinuxScanAdapter()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux scanner adapter доступен только на Linux.");
        }

        scanningContext = new ScanningContext(new GtkImageContext());
        controller = new ScanController(scanningContext);
    }

    public async Task<IReadOnlyList<ScannerDevice>> GetScannersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = await controller.GetDeviceList(Driver.Sane);
        cancellationToken.ThrowIfCancellationRequested();
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

        var devices = await controller.GetDeviceList(Driver.Sane);
        cancellationToken.ThrowIfCancellationRequested();
        var device = devices.SingleOrDefault(candidate =>
            string.Equals(candidate.ID, scannerId, StringComparison.Ordinal));

        if (device is null)
        {
            throw new InvalidOperationException($"Сканер с идентификатором '{scannerId}' не найден.");
        }

        var caps = await controller.GetCaps(device);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSourceSupported(caps.PaperSourceCaps, source);

        var options = new ScanOptions
        {
            Driver = Driver.Sane,
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

    private static void EnsureSourceSupported(PaperSourceCaps? paperSourceCaps, ScanSource source)
    {
        if (paperSourceCaps is null)
        {
            throw new InvalidOperationException(
                "SANE не сообщил доступные источники; явный источник сканирования не может быть подтверждён.");
        }

        var supported = source switch
        {
            ScanSource.Glass => paperSourceCaps.SupportsFlatbed,
            ScanSource.Feeder => paperSourceCaps.SupportsFeeder,
            ScanSource.Duplex => paperSourceCaps.SupportsDuplex,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };

        if (!supported)
        {
            throw new InvalidOperationException(
                $"Запрошенный источник '{source}' не поддерживается выбранным SANE-устройством.");
        }
    }

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
