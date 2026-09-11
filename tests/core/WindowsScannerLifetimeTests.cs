using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsScannerLifetimeTests
{
    [Fact]
    public void Holder_StartsWithoutConstructingAdapter()
    {
        var created = 0;
        var holder = new WindowsScanAdapterHolder(() =>
        {
            Interlocked.Increment(ref created);
            return new FakeScanAdapter();
        });

        Assert.Equal(0, created);
        Assert.False(holder.IsCreated);
    }

    [Fact]
    public async Task HostedShutdown_WithoutPriorScannerUse_DoesNotConstructAdapter()
    {
        var created = 0;
        var holder = new WindowsScanAdapterHolder(() =>
        {
            Interlocked.Increment(ref created);
            return new FakeScanAdapter();
        });
        var hostedService = new WindowsScannerShutdownHostedService(holder);

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(0, created);
        Assert.False(holder.IsCreated);
    }

    [Fact]
    public async Task ConcurrentGetOrCreate_ConstructsExactlyOneAdapter()
    {
        var created = 0;
        var adapter = new FakeScanAdapter();
        var holder = new WindowsScanAdapterHolder(() =>
        {
            Interlocked.Increment(ref created);
            return adapter;
        });

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(holder.GetOrCreate))
            .ToArray();
        var resolved = await Task.WhenAll(tasks);

        Assert.Equal(1, created);
        Assert.All(resolved, instance => Assert.Same(adapter, instance));
        Assert.True(holder.IsCreated);
    }

    [Fact]
    public void ShutdownIfCreated_IsIdempotentAndDisposesExactlyOnce()
    {
        var adapter = new FakeScanAdapter();
        var holder = new WindowsScanAdapterHolder(() => adapter);
        Assert.Same(adapter, holder.GetOrCreate());

        holder.ShutdownIfCreated();
        holder.ShutdownIfCreated();

        Assert.Equal(1, adapter.DisposeCalls);
    }

    [Fact]
    public void GetOrCreate_AfterShutdownBegins_FailsClosedWithoutConstruction()
    {
        var created = 0;
        var holder = new WindowsScanAdapterHolder(() =>
        {
            Interlocked.Increment(ref created);
            return new FakeScanAdapter();
        });

        holder.ShutdownIfCreated();

        Assert.Throws<InvalidOperationException>(() => holder.GetOrCreate());
        Assert.Equal(0, created);
    }

    [Fact]
    public void HostedService_DependsOnlyOnHolder_NotOnScanAdapter()
    {
        var constructor = Assert.Single(typeof(WindowsScannerShutdownHostedService).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(WindowsScanAdapterHolder), parameter.ParameterType);
    }

    private sealed class FakeScanAdapter : IScanAdapter, IDisposable
    {
        private int disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref disposeCalls);

        public Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannerDiscoveryResult([], [], isAvailable: true));

        public Task<Stream> ScanAsync(string scannerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public void Dispose() => Interlocked.Increment(ref disposeCalls);
    }
}
