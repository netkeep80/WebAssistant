using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebAssistant.Scanning;

internal sealed class WindowsScannerShutdownHostedService : IHostedService
{
    private readonly WindowsScanAdapterHolder holder;
    private readonly ILogger<WindowsScannerShutdownHostedService> logger;
    private readonly IHostApplicationLifetime applicationLifetime;
    private IDisposable? stoppingRegistration;
    private int shutdownStarted;

    public WindowsScannerShutdownHostedService(
        WindowsScanAdapterHolder holder,
        ILogger<WindowsScannerShutdownHostedService> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        this.holder = holder;
        this.logger = logger;
        this.applicationLifetime = applicationLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        stoppingRegistration =
            applicationLifetime.ApplicationStopping.Register(ShutdownOnce);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ShutdownOnce();
        stoppingRegistration?.Dispose();
        return Task.CompletedTask;
    }

    private void ShutdownOnce()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("scanner.shutdown stage=start trigger=applicationStopping");

        try
        {
            var disposed = holder.ShutdownIfCreated();
            stopwatch.Stop();
            logger.LogInformation(
                "scanner.shutdown stage=complete outcome={Outcome} durationMs={DurationMs}",
                disposed ? "success" : "notCreated",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogError(
                "scanner.shutdown stage=complete outcome=failure durationMs={DurationMs} exceptionType={ExceptionType} hresult={HResult}",
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                $"0x{unchecked((uint)exception.HResult):X8}");
            throw;
        }
    }
}
