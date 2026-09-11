namespace WebAssistant.Http;

internal sealed class ScanRequest
{
    public string? ScannerId { get; init; }

    public string? Source { get; init; }

    public ScanSettings? Settings { get; init; }
}

internal sealed class ScanSettings
{
    public bool? Duplex { get; init; }
}
