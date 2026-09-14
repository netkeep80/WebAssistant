namespace WebAssistant.Scanning;

internal interface IScanAdapter
{
    Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default);

    async Task<ScannerDevice?> GetScannerCapabilitiesAsync(
        string scannerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerId);
        var discovery = await GetScannersAsync(cancellationToken);
        var scanner = discovery.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, scannerId, StringComparison.Ordinal));
        if (scanner is not null)
        {
            return scanner;
        }

        if (ScannerIdentity.TryParse(scannerId, out var backend) &&
            discovery.Warnings.Any(warning =>
                warning.Backend == backend && warning.Code == "enumerationFailed"))
        {
            throw new ScannerBackendUnavailableException(backend);
        }

        return null;
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
