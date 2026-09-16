namespace WebAssistant.Scanning;

internal interface IScanAdapter
{
    Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default);

    Task<ScannerDevice?> GetScannerAsync(
        string scannerId,
        CancellationToken cancellationToken = default) =>
        GetScannerCapabilitiesAsync(scannerId, cancellationToken);

    Task<ScannerDevice?> GetScannerCapabilitiesAsync(
        string scannerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ScannerDevice?>(
            new NotSupportedException(
                "Адаптер обязан явно реализовать получение capabilities выбранного scanner endpoint без global discovery fallback."));
    }

    Task<Stream> ScanAsync(string scannerId, CancellationToken cancellationToken = default);

    Task<Stream> ScanAsync(
        string scannerId,
        ScanSource source,
        CancellationToken cancellationToken = default)
    {
        if (source != ScanSource.Glass)
        {
            throw new NotSupportedException(
                $"Источник сканирования '{source}' не поддерживается адаптером.");
        }

        return ScanAsync(scannerId, cancellationToken);
    }

    Task<Stream> ScanAsync(
        string scannerId,
        ScanSource source,
        ScannerEffectiveSettings settings,
        CancellationToken cancellationToken = default) =>
        ScanAsync(scannerId, source, cancellationToken);
}
