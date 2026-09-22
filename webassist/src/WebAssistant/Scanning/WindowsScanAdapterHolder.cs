using Microsoft.Extensions.Logging;
using WebAssistant.Runtime;

namespace WebAssistant.Scanning;

internal sealed class WindowsScanAdapterHolder
{
    private readonly object gate = new();
    private readonly Func<IScanAdapter> factory;
    private IScanAdapter? adapter;
    private bool shutdownStarted;

    public WindowsScanAdapterHolder(
        ILogger<WindowsScanAdapter> logger,
        RuntimeDiagnosticSnapshotProvider diagnostics)
        : this(() => new WindowsScanAdapter(logger, diagnostics))
    {
    }

    internal WindowsScanAdapterHolder(Func<IScanAdapter> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
    }

    internal bool IsCreated
    {
        get
        {
            lock (gate)
            {
                return adapter is not null;
            }
        }
    }

    internal IScanAdapter GetOrCreate()
    {
        lock (gate)
        {
            if (shutdownStarted)
            {
                throw new InvalidOperationException(
                    "Windows scanner subsystem is shutting down and cannot create a new adapter.");
            }

            return adapter ??= factory();
        }
    }

    internal bool ShutdownIfCreated()
    {
        IScanAdapter? adapterToDispose;
        lock (gate)
        {
            if (shutdownStarted)
            {
                return false;
            }

            shutdownStarted = true;
            adapterToDispose = adapter;
        }

        if (adapterToDispose is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return adapterToDispose is not null;
    }
}
