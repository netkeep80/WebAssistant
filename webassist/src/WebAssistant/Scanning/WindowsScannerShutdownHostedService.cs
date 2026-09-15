using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebAssistant.Scanning;

internal sealed class WindowsScannerShutdownHostedService : IHostedService
{
    private readonly WindowsScanAdapterHolder holder;
    private readonly ILogger<WindowsScannerShutdownHostedService> logger;

    public WindowsScannerShutdownHostedService(
        WindowsScanAdapterHolder holder,
        ILogger<WindowsScannerShutdownHostedService> logger)
    {
        this.holder = holder;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var disposed = holder.ShutdownIfCreated();
            stopwatch.Stop();
            logger.LogInformation(
                "scanner.shutdown outcome={Outcome} durationMs={DurationMs}",
                disposed ? "success" : "notCreated",
                stopwatch.ElapsedMilliseconds);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogError(
                "scanner.shutdown outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                $"0x{unchecked((uint)exception.HResult):X8}");
            throw;
        }
    }
}
