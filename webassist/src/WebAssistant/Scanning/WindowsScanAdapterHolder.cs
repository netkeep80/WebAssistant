namespace WebAssistant.Scanning;

internal sealed class WindowsScanAdapterHolder
{
    private readonly object gate = new();
    private readonly Func<IScanAdapter> factory;
    private IScanAdapter? adapter;
    private bool shutdownStarted;

    public WindowsScanAdapterHolder()
        : this(() => new WindowsScanAdapter())
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

    internal void ShutdownIfCreated()
    {
        IScanAdapter? adapterToDispose;
        lock (gate)
        {
            if (shutdownStarted)
            {
                return;
            }

            shutdownStarted = true;
            adapterToDispose = adapter;
        }

        if (adapterToDispose is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
