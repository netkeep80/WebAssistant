using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class ScannerCapabilityAvailabilityTests
{
    [Fact]
    public async Task WindowsCapabilityProbeFailure_ReturnsUnavailableSnapshot()
    {
        var scannerId = ScannerIdentity.Create(ScannerBackend.Twain, "twain-native-1");

        var scanner = await WindowsScanAdapter.ResolveCapabilitiesAsync(
            scannerId,
            driver => Task.FromResult(driver == Driver.Twain
                ? new List<ScanDevice>
                {
                    new(Driver.Twain, "twain-native-1", "TWAIN scanner")
                }
                : throw new InvalidOperationException("wrong backend")),
            (_, _) => Task.FromException<ScanCaps>(
                new InvalidOperationException("native capability probe failed")));

        Assert.NotNull(scanner);
        Assert.Equal(ScannerCapabilityState.Unavailable, scanner.CapabilityState);
        Assert.Null(scanner.Capabilities);
        Assert.False(scanner.SupportsFlatbed);
        Assert.False(scanner.SupportsFeeder);
        Assert.False(scanner.SupportsDuplex);
    }

    [Fact]
    public async Task SettingsEndpoint_UnavailableSnapshot_IsExplicitAndNotUnsupportedModes()
    {
        var scannerId = ScannerIdentity.Create(ScannerBackend.Twain, "twain-native-1");
        var adapter = new UnavailableCapabilitiesAdapter(scannerId);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/v1/scanners/{scannerId}/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", json.RootElement.GetProperty("capabilityState").GetString());
        Assert.Empty(json.RootElement.GetProperty("modes").EnumerateArray());
    }

    [Fact]
    public async Task AutoScan_UnavailableCapabilities_Returns503WithoutAcquisition()
    {
        var scannerId = ScannerIdentity.Create(ScannerBackend.Twain, "twain-native-1");
        var adapter = new UnavailableCapabilitiesAdapter(scannerId);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId,
            source = "auto"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    private static WebApplicationFactory<Program> CreateFactory(IScanAdapter adapter) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanAdapter>();
                services.AddSingleton(adapter);
            });
        });

    private sealed class UnavailableCapabilitiesAdapter(string scannerId) : IScanAdapter
    {
        private int scanCalls;

        internal int ScanCalls => Volatile.Read(ref scanCalls);

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannerDiscoveryResult([]));

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string requestedScannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScannerDevice?>(requestedScannerId == scannerId
                ? new ScannerDevice(
                    scannerId,
                    "TWAIN scanner",
                    ScannerBackend.Twain,
                    CapabilityState: ScannerCapabilityState.Unavailable)
                : null);

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref scanCalls);
            return Task.FromResult<Stream>(
                new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray(), writable: false));
        }
    }
}

#pragma warning restore CA2252
