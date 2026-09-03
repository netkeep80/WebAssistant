namespace WebAssistant.Logging;

internal sealed class DailyLogReader(string logDirectory)
{
    internal async Task<string?> ReadAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var file = Path.Combine(logDirectory, $"webassistant-{date:yyyy-MM-dd}.log");
        if (!File.Exists(file))
        {
            return null;
        }

        return await File.ReadAllTextAsync(file, cancellationToken);
    }
}
