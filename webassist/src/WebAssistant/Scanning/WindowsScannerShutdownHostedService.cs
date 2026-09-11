using Microsoft.Extensions.Hosting;

namespace WebAssistant.Scanning;

internal sealed class WindowsScannerShutdownHostedService : IHostedService
{
    private readonly WindowsScanAdapterHolder holder;

    public WindowsScannerShutdownHostedService(WindowsScanAdapterHolder holder)
    {
        this.holder = holder;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        holder.ShutdownIfCreated();
        return Task.CompletedTask;
    }
}
