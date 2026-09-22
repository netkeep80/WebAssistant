using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        var hostedService = new WindowsScannerShutdownHostedService(
            holder,
            new CaptureLogger<WindowsScannerShutdownHostedService>(),
            new FakeHostApplicationLifetime());

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
    public async Task HostedShutdown_LogsDurationAndOutcomeWithoutCreatingUnusedAdapter()
    {
        var created = 0;
        var logger = new CaptureLogger<WindowsScannerShutdownHostedService>();
        var holder = new WindowsScanAdapterHolder(() =>
        {
            Interlocked.Increment(ref created);
            return new FakeScanAdapter();
        });
        var hostedService = new WindowsScannerShutdownHostedService(
            holder,
            logger,
            new FakeHostApplicationLifetime());

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(0, created);
        Assert.Contains(logger.Messages, message =>
            message.Contains("scanner.shutdown", StringComparison.Ordinal) &&
            message.Contains("outcome=notCreated", StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedShutdown_LogsSuccessfulOwnedAdapterDisposal()
    {
        var adapter = new FakeScanAdapter();
        var logger = new CaptureLogger<WindowsScannerShutdownHostedService>();
        var holder = new WindowsScanAdapterHolder(() => adapter);
        Assert.Same(adapter, holder.GetOrCreate());
        var hostedService = new WindowsScannerShutdownHostedService(
            holder,
            logger,
            new FakeHostApplicationLifetime());

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(1, adapter.DisposeCalls);
        Assert.Contains(logger.Messages, message =>
            message.Contains("scanner.shutdown", StringComparison.Ordinal) &&
            message.Contains("outcome=success", StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplicationStopping_DisposesOwnedAdapterBeforeStopAsync()
    {
        var adapter = new FakeScanAdapter();
        var holder = new WindowsScanAdapterHolder(() => adapter);
        Assert.Same(adapter, holder.GetOrCreate());
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new CaptureLogger<WindowsScannerShutdownHostedService>();
        var hostedService = new WindowsScannerShutdownHostedService(
            holder,
            logger,
            lifetime);

        await hostedService.StartAsync(CancellationToken.None);
        lifetime.StopApplication();

        Assert.Equal(1, adapter.DisposeCalls);
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                "scanner.shutdown stage=start trigger=applicationStopping",
                StringComparison.Ordinal));

        await hostedService.StopAsync(CancellationToken.None);
        Assert.Equal(1, adapter.DisposeCalls);
    }

    [Fact]
    public void HostedService_DependsOnHolderAndLogger_NotOnScanAdapter()
    {
        var constructor = Assert.Single(typeof(WindowsScannerShutdownHostedService).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(WindowsScanAdapterHolder), parameters[0].ParameterType);
        Assert.Equal(typeof(ILogger<WindowsScannerShutdownHostedService>), parameters[1].ParameterType);
        Assert.Equal(typeof(IHostApplicationLifetime), parameters[2].ParameterType);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication() => stopping.Cancel();
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

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
