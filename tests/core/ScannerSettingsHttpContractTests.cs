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

public sealed class ScannerSettingsHttpContractTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%%EOF"u8.ToArray();

    [Fact]
    public async Task ScannerSettingsSchema_IsPublishedUnderVersionedApi()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanner-settings/schema");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("fields").TryGetProperty("dpi", out _));
        Assert.True(document.RootElement.GetProperty("fields").TryGetProperty("colorMode", out _));
        Assert.True(document.RootElement.GetProperty("fields").TryGetProperty("paperSize", out _));
    }

    [Fact]
    public async Task SelectedScanner_SettingsProjection_IsPublished()
    {
        var scanner = CreateScanner();
        using var factory = CreateFactory(scanner);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/v1/scanners/{Uri.EscapeDataString(scanner.Id)}/settings");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(scanner.Id, document.RootElement.GetProperty("scannerId").GetString());
        Assert.True(document.RootElement.GetProperty("modes").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Scan_InvalidDpi_IsBadRequestBeforePhysicalAcquisition()
    {
        var scanner = CreateScanner();
        var adapter = new FakeScanAdapter(new ScannerDiscoveryResult([scanner]));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = scanner.Id,
            source = "flatbed",
            settings = new
            {
                duplex = false,
                dpi = 0
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_UnknownColorMode_IsBadRequestBeforePhysicalAcquisition()
    {
        var scanner = CreateScanner();
        var adapter = new FakeScanAdapter(new ScannerDiscoveryResult([scanner]));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = scanner.Id,
            source = "flatbed",
            settings = new
            {
                duplex = false,
                colorMode = "gray"
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    private static ScannerDevice CreateScanner() =>
        new(
            ScannerIdentity.Create(ScannerBackend.Wia, "settings-test-scanner"),
            "Settings test scanner",
            ScannerBackend.Wia,
            SupportsFlatbed: true,
            SupportsFeeder: true,
            SupportsDuplex: true,
            FeederPaperState.Unknown);

    private static WebApplicationFactory<Program> CreateFactory(params ScannerDevice[] scanners) =>
        CreateFactory(new FakeScanAdapter(new ScannerDiscoveryResult(scanners)));

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

    private sealed class FakeScanAdapter(ScannerDiscoveryResult discovery) : IScanAdapter
    {
        private int scanCalls;

        public int ScanCalls => Volatile.Read(ref scanCalls);

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(discovery);
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            ScanAsync(scannerId, ScanSource.Glass, cancellationToken);

        public Task<Stream> ScanAsync(
            string scannerId,
            ScanSource source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref scanCalls);
            return Task.FromResult<Stream>(new MemoryStream(PdfBytes, writable: false));
        }
    }
}
