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
    public async Task SelectedScanner_SettingsProjection_PreservesModeSpecificCapabilitiesAndSafeAutoIntersection()
    {
        var scanner = CreateScanner();
        using var factory = CreateFactory(scanner);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/v1/scanners/{Uri.EscapeDataString(scanner.Id)}/settings");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(scanner.Id, document.RootElement.GetProperty("scannerId").GetString());
        var modes = document.RootElement.GetProperty("modes").EnumerateArray().ToArray();
        Assert.Equal(4, modes.Length);

        var auto = Assert.Single(modes, mode => mode.GetProperty("mode").GetString() == "auto");
        Assert.Equal("auto", auto.GetProperty("source").GetString());
        Assert.False(auto.GetProperty("duplex").GetBoolean());
        var settings = auto.GetProperty("settings");
        Assert.Equal(new[] { 300 }, settings.GetProperty("dpi").GetProperty("values").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(300, settings.GetProperty("dpi").GetProperty("default").GetInt32());
        Assert.Equal(new[] { "grayscale" }, settings.GetProperty("colorMode").GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("grayscale", settings.GetProperty("colorMode").GetProperty("default").GetString());
        Assert.Equal(new[] { "a4" }, settings.GetProperty("paperSize").GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("a4", settings.GetProperty("paperSize").GetProperty("default").GetString());
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
            settings = new { duplex = false, dpi = 0 }
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
            settings = new { duplex = false, colorMode = "gray" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_ValidButUnsupportedDpi_IsUnprocessableBeforePhysicalAcquisition()
    {
        var scanner = CreateScanner();
        var adapter = new FakeScanAdapter(new ScannerDiscoveryResult([scanner]));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = scanner.Id,
            source = "flatbed",
            settings = new { duplex = false, dpi = 200 }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_AutoOmittedSettings_UsesSafeIntersectionDefaults()
    {
        var scanner = CreateScanner();
        var adapter = new FakeScanAdapter(new ScannerDiscoveryResult([scanner]));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new { scannerId = scanner.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, adapter.ScanCalls);
        Assert.Equal(ScanSource.Glass, adapter.LastSource);
        Assert.Equal(300, adapter.LastSettings.Dpi);
        Assert.Equal(ScannerColorMode.Grayscale, adapter.LastSettings.ColorMode);
        Assert.Equal(ScannerPaperSize.A4, adapter.LastSettings.PaperSize);
    }

    [Fact]
    public async Task Scan_ExplicitSupportedSettings_ArePassedExactlyToAdapter()
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
                dpi = 600,
                colorMode = "color",
                paperSize = "letter"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, adapter.ScanCalls);
        Assert.Equal(ScanSource.Glass, adapter.LastSource);
        Assert.Equal(600, adapter.LastSettings.Dpi);
        Assert.Equal(ScannerColorMode.Color, adapter.LastSettings.ColorMode);
        Assert.Equal(ScannerPaperSize.Letter, adapter.LastSettings.PaperSize);
    }

    private static ScannerDevice CreateScanner()
    {
        var flatbed = new ScannerSourceCapabilities(
            [100, 300, 600],
            [ScannerColorMode.Color, ScannerColorMode.Grayscale],
            [ScannerPaperSize.Letter, ScannerPaperSize.A4]);
        var feeder = new ScannerSourceCapabilities(
            [200, 300],
            [ScannerColorMode.Grayscale, ScannerColorMode.BlackAndWhite],
            [ScannerPaperSize.A4]);
        var duplex = new ScannerSourceCapabilities(
            [300],
            [ScannerColorMode.Grayscale],
            [ScannerPaperSize.A4]);

        return new ScannerDevice(
            ScannerIdentity.Create(ScannerBackend.Wia, "settings-test-scanner"),
            "Settings test scanner",
            ScannerBackend.Wia,
            SupportsFlatbed: true,
            SupportsFeeder: true,
            SupportsDuplex: true,
            FeederPaperState.Unknown,
            new ScannerEndpointCapabilities(flatbed, feeder, duplex));
    }

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

        public ScanSource? LastSource { get; private set; }
        public ScannerEffectiveSettings LastSettings { get; private set; }
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
            ScanAsync(scannerId, ScanSource.Glass, ScannerEffectiveSettings.Unspecified, cancellationToken);

        public Task<Stream> ScanAsync(
            string scannerId,
            ScanSource source,
            CancellationToken cancellationToken = default) =>
            ScanAsync(scannerId, source, ScannerEffectiveSettings.Unspecified, cancellationToken);

        public Task<Stream> ScanAsync(
            string scannerId,
            ScanSource source,
            ScannerEffectiveSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSource = source;
            LastSettings = settings;
            Interlocked.Increment(ref scanCalls);
            return Task.FromResult<Stream>(new MemoryStream(PdfBytes, writable: false));
        }
    }
}
