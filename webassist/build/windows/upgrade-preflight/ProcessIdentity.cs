namespace WebAssistant.UpgradePreflight;

internal sealed record ProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    string ImagePath,
    DateTimeOffset StartTimeUtc);
