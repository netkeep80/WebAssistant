namespace WebAssistant.UpgradePreflight;

internal static class Program
{
    private const string DiagnosticFileName = "WebAssistant-UpgradePreflight.log";

    private static async Task<int> Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            const string message = "preflight-fail reason=windows-only";
            Console.Error.WriteLine(message);
            TryWriteDiagnostic(message);
            return 1;
        }

        var policy = new UpgradePreflightPolicy(
            ServiceStopTimeout: TimeSpan.FromSeconds(15),
            WorkerGraceTimeout: TimeSpan.FromSeconds(3),
            PostTerminateTimeout: TimeSpan.FromSeconds(2),
            PollInterval: TimeSpan.FromMilliseconds(100));

        try
        {
            const string startMessage = "preflight-start service=WebAssistant";
            Console.WriteLine(startMessage);
            TryWriteDiagnostic(startMessage);

            using var environment = new RetainedServiceProcessUpgradeEnvironment();
            var orchestrator = new UpgradePreflightOrchestrator(environment, policy);
            await orchestrator.RunAsync(CancellationToken.None);

            const string passMessage = "preflight-pass";
            Console.WriteLine(passMessage);
            TryWriteDiagnostic(passMessage);
            return 0;
        }
        catch (Exception exception)
        {
            var message =
                $"preflight-fail type={exception.GetType().Name} message={Sanitize(exception.Message)}";
            Console.Error.WriteLine(message);
            TryWriteDiagnostic(message);
            return 1;
        }
    }

    private static void TryWriteDiagnostic(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), DiagnosticFileName);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.UtcNow:O} {Sanitize(message)}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never change upgrade behavior.
        }
    }

    private static string Sanitize(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
