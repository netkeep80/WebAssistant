namespace WebAssistant.Scanning;

internal static class ScannerOperationDeadlines
{
    internal static readonly TimeSpan Discovery = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan Capabilities = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan Acquisition = TimeSpan.FromMinutes(30);
}
