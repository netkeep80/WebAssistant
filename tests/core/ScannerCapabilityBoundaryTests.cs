using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerCapabilityBoundaryTests
{
    [Fact]
    public async Task ScannerList_ReturnsIdentityOnly_WithoutSourcesProjection()
    {
        var adapter = new CapabilityBoundaryFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        var scanners = document.RootElement.GetProperty("scanners").EnumerateArray().ToArray();
        Assert.Equal(2, scanners.Length);
        Assert.All(scanners, scanner =>
        {
            Assert.True(scanner.TryGetProperty("scannerId", out _));
            Assert.True(scanner.TryGetProperty("name", out _));
            Assert.True(scanner.TryGetProperty("backend", out _));
            Assert.False(scanner.TryGetProperty("sources", out _));
        });
        Assert.Equal(0, adapter.CapabilityCalls);
    }

    [Fact]
    public async Task SelectedScannerSettings_ProbesCapabilitiesOnlyForSelectedScanner()
    {
        var adapter = new CapabilityBoundaryFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();
        var selectedId = adapter.RegisteredScanners[1].Id;

        using var response = await client.GetAsync(
            $"/v1/scanners/{Uri.EscapeDataString(selectedId)}/settings");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, adapter.CapabilityCalls);
        Assert.Equal(selectedId, adapter.LastCapabilityScannerId);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(selectedId, document.RootElement.GetProperty("scannerId").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("modes").EnumerateArray());
    }

    private static WebApplicationFactory<Program> CreateFactory(IScanAdapter adapter)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanAdapter>();
                services.AddSingleton(adapter);
            });
        });
    }

    private sealed class CapabilityBoundaryFakeAdapter : IScanAdapter
    {
        private int capabilityCalls;

        internal CapabilityBoundaryFakeAdapter()
        {
            RegisteredScanners =
            [
                new ScannerDevice(
                    ScannerIdentity.Create(ScannerBackend.Wia, "registered-1"),
                    "Registered scanner 1",
                    ScannerBackend.Wia,
                    SupportsFlatbed: false),
                new ScannerDevice(
                    ScannerIdentity.Create(ScannerBackend.Wia, "registered-2"),
                    "Registered scanner 2",
                    ScannerBackend.Wia,
                    SupportsFlatbed: false)
            ];
        }

        internal IReadOnlyList<ScannerDevice> RegisteredScanners { get; }
        internal int CapabilityCalls => Volatile.Read(ref capabilityCalls);
        internal string? LastCapabilityScannerId { get; private set; }

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScannerDiscoveryResult(RegisteredScanners));
        }

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref capabilityCalls);
            LastCapabilityScannerId = scannerId;

            var registered = RegisteredScanners.FirstOrDefault(scanner => scanner.Id == scannerId);
            if (registered is null)
            {
                return Task.FromResult<ScannerDevice?>(null);
            }

            var flatbed = new ScannerSourceCapabilities(
                [300],
                [ScannerColorMode.Grayscale],
                [ScannerPaperSize.A4]);
            return Task.FromResult<ScannerDevice?>(registered with
            {
                SupportsFlatbed = true,
                Capabilities = new ScannerEndpointCapabilities(flatbed, null, null)
            });
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }
}
