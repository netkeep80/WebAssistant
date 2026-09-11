namespace WebAssistant.UpgradePreflight;

internal static class Program
{
    private static async Task<int> Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("preflight-fail reason=windows-only");
            return 1;
        }

        var policy = new UpgradePreflightPolicy(
            ServiceStopTimeout: TimeSpan.FromSeconds(15),
            WorkerGraceTimeout: TimeSpan.FromSeconds(3),
            PostTerminateTimeout: TimeSpan.FromSeconds(2),
            PollInterval: TimeSpan.FromMilliseconds(100));

        try
        {
            Console.WriteLine("preflight-start service=WebAssistant");
            using var environment = new RetainedServiceProcessUpgradeEnvironment();
            var orchestrator = new UpgradePreflightOrchestrator(environment, policy);
            await orchestrator.RunAsync(CancellationToken.None);
            Console.WriteLine("preflight-pass");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"preflight-fail type={exception.GetType().Name} message={Sanitize(exception.Message)}");
            return 1;
        }
    }

    private static string Sanitize(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
