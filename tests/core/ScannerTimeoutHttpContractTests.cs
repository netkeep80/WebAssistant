using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerTimeoutHttpContractTests
{
    [Fact]
    public void OperationDeadlines_AreExplicitAndFinite()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ScannerOperationDeadlines.Discovery);
        Assert.Equal(TimeSpan.FromSeconds(30), ScannerOperationDeadlines.Capabilities);
        Assert.Equal(TimeSpan.FromMinutes(30), ScannerOperationDeadlines.Acquisition);

        Assert.All(
            new[]
            {
                ScannerOperationDeadlines.Discovery,
                ScannerOperationDeadlines.Capabilities,
                ScannerOperationDeadlines.Acquisition
            },
            deadline =>
            {
                Assert.True(deadline > TimeSpan.Zero);
                Assert.NotEqual(Timeout.InfiniteTimeSpan, deadline);
            });
    }

    [Fact]
    public async Task Settings_DeadlineReturnsStableGatewayTimeoutProblem()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Twain,
            "timeout-settings");
        using var factory = CreateFactory(
            new ThrowingAdapter(
                scannerId,
                new ScannerOperationTimeoutException(
                    ScannerOperationKind.Capabilities,
                    ScannerBackend.Twain,
                    ScannerOperationDeadlines.Capabilities)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v1/scanners/{Uri.EscapeDataString(scannerId)}/settings");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertProblemAsync(
            response,
            "scanner_operation_timeout",
            "capabilities");
    }

    [Fact]
    public async Task Settings_WorkerRecoveryFailureReturnsStableServiceUnavailableProblem()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Twain,
            "recovery-settings");
        using var factory = CreateFactory(
            new ThrowingAdapter(
                scannerId,
                new ScannerWorkerRecoveryException(
                    ScannerOperationKind.Capabilities,
                    ScannerBackend.Twain,
                    workerProcessId: 42101,
                    new TimeoutException("worker-alive"))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v1/scanners/{Uri.EscapeDataString(scannerId)}/settings");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertProblemAsync(
            response,
            "scanner_worker_recovery_failed",
            "capabilities");
    }

    [Fact]
    public async Task Scan_AcquisitionDeadlineReturnsStableGatewayTimeoutProblem()
    {
        var scanner = CreateScanner("timeout-scan");
        using var factory = CreateFactory(
            new AcquisitionFailureAdapter(
                scanner,
                new ScannerOperationTimeoutException(
                    ScannerOperationKind.Acquisition,
                    ScannerBackend.Twain,
                    ScannerOperationDeadlines.Acquisition)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/scan",
            new
            {
                scannerId = scanner.Id,
                source = "flatbed",
                settings = new { duplex = false }
            });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertProblemAsync(
            response,
            "scanner_operation_timeout",
            "acquisition");
    }

    [Fact]
    public async Task Scan_WorkerRecoveryFailureReturnsStableServiceUnavailableProblem()
    {
        var scanner = CreateScanner("recovery-scan");
        using var factory = CreateFactory(
            new AcquisitionFailureAdapter(
                scanner,
                new ScannerWorkerRecoveryException(
                    ScannerOperationKind.Acquisition,
                    ScannerBackend.Twain,
                    workerProcessId: 42102,
                    new TimeoutException("worker-alive"))));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/scan",
            new
            {
                scannerId = scanner.Id,
                source = "flatbed",
                settings = new { duplex = false }
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertProblemAsync(
            response,
            "scanner_worker_recovery_failed",
            "acquisition");
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string code,
        string operation)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            operation,
            document.RootElement.GetProperty("operation").GetString());
    }

    private static ScannerDevice CreateScanner(string nativeId) =>
        new(
            ScannerIdentity.Create(ScannerBackend.Twain, nativeId),
            "TWAIN timeout test",
            ScannerBackend.Twain,
            SupportsFlatbed: true,
            SupportsFeeder: false,
            SupportsDuplex: false,
            FeederPaperState.Unknown,
            Capabilities: null,
            CapabilityState: ScannerCapabilityState.Unavailable);

    private static WebApplicationFactory<Program> CreateFactory(IScanAdapter adapter) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanAdapter>();
                services.AddSingleton(adapter);
            });
        });

    private sealed class ThrowingAdapter(
        string scannerId,
        Exception failure) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannerDiscoveryResult([]));

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string requestedScannerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(scannerId, requestedScannerId);
            return Task.FromException<ScannerDevice?>(failure);
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AcquisitionFailureAdapter(
        ScannerDevice scanner,
        Exception failure) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannerDiscoveryResult([scanner]));

        public Task<ScannerDevice?> GetScannerAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScannerDevice?>(
                string.Equals(scanner.Id, scannerId, StringComparison.Ordinal)
                    ? scanner
                    : null);

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            GetScannerAsync(scannerId, cancellationToken);

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            ScanAsync(
                scannerId,
                ScanSource.Glass,
                ScannerEffectiveSettings.Unspecified,
                cancellationToken);

        public Task<Stream> ScanAsync(
            string scannerId,
            ScanSource source,
            ScannerEffectiveSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Stream>(failure);
    }
}
