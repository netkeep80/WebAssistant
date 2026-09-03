namespace WebAssistant.Scanning;

internal interface IScanAdapter
{
    Task<IReadOnlyList<ScannerDevice>> GetScannersAsync(CancellationToken cancellationToken = default);

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
}
