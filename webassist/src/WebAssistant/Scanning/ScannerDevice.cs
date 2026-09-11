using System.Collections;

namespace WebAssistant.Scanning;

internal sealed record ScannerDevice(
    string Id,
    string Name,
    ScannerBackend Backend = ScannerBackend.Sane,
    bool SupportsFlatbed = true,
    bool SupportsFeeder = false,
    bool SupportsDuplex = false,
    FeederPaperState FeederPaperState = FeederPaperState.Unknown,
    ScannerEndpointCapabilities? Capabilities = null);

internal sealed record ScannerDiscoveryWarning(ScannerBackend Backend, string Code);

internal sealed class ScannerDiscoveryResult : IReadOnlyList<ScannerDevice>
{
    internal ScannerDiscoveryResult(
        IReadOnlyList<ScannerDevice> scanners,
        IReadOnlyList<ScannerDiscoveryWarning>? warnings = null,
        bool isAvailable = true)
    {
        Scanners = scanners;
        Warnings = warnings ?? [];
        IsAvailable = isAvailable;
    }

    internal IReadOnlyList<ScannerDevice> Scanners { get; }
    internal IReadOnlyList<ScannerDiscoveryWarning> Warnings { get; }
    internal bool IsAvailable { get; }

    public int Count => Scanners.Count;
    public ScannerDevice this[int index] => Scanners[index];
    public IEnumerator<ScannerDevice> GetEnumerator() => Scanners.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
