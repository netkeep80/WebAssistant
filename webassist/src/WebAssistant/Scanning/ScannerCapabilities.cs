namespace WebAssistant.Scanning;

internal sealed record ScannerSourceCapabilities(
    IReadOnlyList<int> DpiValues,
    IReadOnlyList<ScannerColorMode> ColorModes,
    IReadOnlyList<ScannerPaperSize> PaperSizes)
{
    internal static ScannerSourceCapabilities Empty { get; } = new([], [], []);
}

internal sealed record ScannerEndpointCapabilities(
    ScannerSourceCapabilities? Flatbed,
    ScannerSourceCapabilities? Feeder,
    ScannerSourceCapabilities? Duplex)
{
    internal static ScannerEndpointCapabilities Empty { get; } = new(null, null, null);
}

internal enum ScannerRequestMode
{
    Auto,
    Flatbed,
    Feeder,
    FeederDuplex
}

internal sealed record ScannerModeCapabilities(
    ScannerRequestMode Mode,
    string Source,
    bool Duplex,
    ScannerSourceCapabilities Settings);
