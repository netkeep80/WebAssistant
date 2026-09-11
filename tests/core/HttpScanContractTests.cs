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

public sealed class HttpScanContractTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%%EOF"u8.ToArray();

    [Fact]
    public async Task VersionedHealth_WorksAndUnversionedRoutesDoNotExist()
    {
        using var factory = CreateFactory(FakeScanAdapter.WithPdf([], PdfBytes));
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/v1/health");
        using var oldHealth = await client.GetAsync("/health");
        using var oldScan = await client.PostAsync("/scan", null);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldHealth.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldScan.StatusCode);
    }

    [Fact]
    public async Task Scanners_ReturnsNormalizedEnvelopeCapabilitiesAndWarnings()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Первый",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present);
        var adapter = FakeScanAdapter.WithDiscovery(
            new ScannerDiscoveryResult(
                [scanner],
                [new ScannerDiscoveryWarning(ScannerBackend.Twain, "enumerationFailed")]),
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scanners = document.RootElement.GetProperty("scanners");
        var item = Assert.Single(scanners.EnumerateArray().ToArray());
        Assert.Equal(scanner.Id, item.GetProperty("scannerId").GetString());
        Assert.Equal("Первый", item.GetProperty("name").GetString());
        Assert.Equal("wia", item.GetProperty("backend").GetString());
        var sources = item.GetProperty("sources");
        Assert.True(sources.GetProperty("flatbed").GetBoolean());
        Assert.True(sources.GetProperty("feeder").GetBoolean());
        Assert.True(sources.GetProperty("duplex").GetBoolean());

        var warning = Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray().ToArray());
        Assert.Equal("twain", warning.GetProperty("backend").GetString());
        Assert.Equal("enumerationFailed", warning.GetProperty("code").GetString());
        Assert.DoesNotContain("native", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feederPaperState", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scanners_UnavailableDiscovery_ReturnsServiceUnavailable()
    {
        var adapter = FakeScanAdapter.WithDiscovery(
            new ScannerDiscoveryResult(
                [],
                [
                    new ScannerDiscoveryWarning(ScannerBackend.Wia, "enumerationFailed"),
                    new ScannerDiscoveryWarning(ScannerBackend.Twain, "enumerationFailed")
                ],
                isAvailable: false),
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Scan_RequiresJsonBodyAndScannerId()
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var noBody = await client.PostAsync("/v1/scan", null);
        using var noScannerId = await client.PostAsJsonAsync("/v1/scan", new { source = "auto" });
        using var blankScannerId = await client.PostAsJsonAsync("/v1/scan", new { scannerId = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noScannerId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blankScannerId.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_MinimalRequest_DefaultsToAutoAndSimplex()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new { scannerId = scanner.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scanner.Id, adapter.LastScannerId);
        Assert.Equal(ScanSource.Feeder, adapter.LastSource);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Theory]
    [InlineData("Absent")]
    [InlineData("Unknown")]
    public async Task Scan_AutoWithoutProvenPaperInDualSource_UsesFlatbed(string paperStateName)
    {
        var paperState = Enum.Parse<FeederPaperState>(paperStateName);
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: false,
            paperState);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "auto"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ScanSource.Glass, adapter.LastSource);
    }

    [Fact]
    public async Task Scan_ExplicitFlatbedAndFeederMapToExactConcreteSources()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var flatbed = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "flatbed"
        });
        Assert.Equal(HttpStatusCode.OK, flatbed.StatusCode);
        Assert.Equal(ScanSource.Glass, adapter.LastSource);

        using var feeder = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "feeder"
        });
        Assert.Equal(HttpStatusCode.OK, feeder.StatusCode);
        Assert.Equal(ScanSource.Feeder, adapter.LastSource);
    }

    [Fact]
    public async Task Scan_FeederDuplexMapsToConcreteDuplexSource()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Unknown);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "feeder",
            settings = new { duplex = true }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ScanSource.Duplex, adapter.LastSource);
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("Flatbed")]
    [InlineData("FEEDER")]
    [InlineData("glass")]
    [InlineData("")]
    public async Task Scan_SourceMustBeExactLowercasePublicValue(string source)
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new { scannerId = scanner.Id, source });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("flatbed")]
    public async Task Scan_DuplexWithNonFeederSource_IsBadRequest(string source)
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source,
            settings = new { duplex = true }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_UnsupportedExplicitSource_IsUnprocessableBeforeAcquisition()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: false,
            supportsDuplex: false,
            FeederPaperState.Unknown);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "feeder"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_UnsupportedDuplex_IsUnprocessableBeforeAcquisition()
    {
        var scanner = CreateScanner(
            ScannerBackend.Wia,
            "wia-native-1",
            "Сканер",
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: false,
            FeederPaperState.Unknown);
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "feeder",
            settings = new { duplex = true }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_MalformedScannerId_IsBadRequestBeforeDiscoveryOrAcquisition()
    {
        var adapter = FakeScanAdapter.WithPdf([], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new { scannerId = "scanner-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.DiscoveryCalls);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_ValidScannerIdAbsentFromAvailableBackend_IsNotFound()
    {
        var existing = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var missingId = ScannerIdentity.Create(ScannerBackend.Wia, "wia-native-missing");
        var adapter = FakeScanAdapter.WithPdf([existing], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new { scannerId = missingId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_IndicatedBackendUnavailable_IsServiceUnavailable()
    {
        var twain = CreateScanner(ScannerBackend.Twain, "twain-native-1", "TWAIN");
        var requestedWiaId = ScannerIdentity.Create(ScannerBackend.Wia, "wia-native-missing");
        var adapter = FakeScanAdapter.WithDiscovery(
            new ScannerDiscoveryResult(
                [twain],
                [new ScannerDiscoveryWarning(ScannerBackend.Wia, "enumerationFailed")]),
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new { scannerId = requestedWiaId });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task ConcurrentScan_ReturnsBusyWithoutSecondAcquisition()
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScanAdapter(
            new ScannerDiscoveryResult([scanner]),
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return new MemoryStream(PdfBytes, writable: false);
            });
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        var firstRequest = PostScanAsync(client, new { scannerId = scanner.Id, source = "flatbed" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var secondResponse = await PostScanAsync(client, new
            {
                scannerId = scanner.Id,
                source = "flatbed"
            });

            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
            Assert.Equal(1, adapter.ScanCalls);
        }
        finally
        {
            release.TrySetResult(true);
        }

        using var firstResponse = await firstRequest;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_SuccessReturnsRawPdf()
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var adapter = FakeScanAdapter.WithPdf([scanner], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "flatbed"
        });
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PdfBytes, body);
    }

    [Theory]
    [InlineData("/v1/scan/feeder")]
    [InlineData("/v1/scan/duplex")]
    public async Task SupersededSourceSpecificRoutes_DoNotExist(string route)
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        using var factory = CreateFactory(FakeScanAdapter.WithPdf([scanner], PdfBytes));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(route, new { scannerId = scanner.Id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdapterFailure_DoesNotBecomeSuccessfulPdf()
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var adapter = new FakeScanAdapter(
            new ScannerDiscoveryResult([scanner]),
            (_, _, _) => Task.FromException<Stream>(
                new InvalidOperationException("scanner failure")));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "flatbed"
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EmptyPdf_DoesNotBecomeSuccessfulDocument()
    {
        var scanner = CreateScanner(ScannerBackend.Wia, "wia-native-1", "Сканер");
        var adapter = FakeScanAdapter.WithPdf([scanner], []);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await PostScanAsync(client, new
        {
            scannerId = scanner.Id,
            source = "flatbed"
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FilesystemRoutes_AreNotExposedYet()
    {
        using var factory = CreateFactory(FakeScanAdapter.WithPdf([], PdfBytes));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/files");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static ScannerDevice CreateScanner(
        ScannerBackend backend,
        string nativeId,
        string name,
        bool supportsFlatbed = true,
        bool supportsFeeder = false,
        bool supportsDuplex = false,
        FeederPaperState paperState = FeederPaperState.Unknown) =>
        new(
            ScannerIdentity.Create(backend, nativeId),
            name,
            backend,
            supportsFlatbed,
            supportsFeeder,
            supportsDuplex,
            paperState);

    private static Task<HttpResponseMessage> PostScanAsync(HttpClient client, object request) =>
        client.PostAsJsonAsync("/v1/scan", request);

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

    private sealed class FakeScanAdapter(
        ScannerDiscoveryResult discovery,
        Func<string, ScanSource, CancellationToken, Task<Stream>> scanAsync) : IScanAdapter
    {
        private int discoveryCalls;
        private int scanCalls;

        public string? LastScannerId { get; private set; }
        public ScanSource? LastSource { get; private set; }
        public int DiscoveryCalls => Volatile.Read(ref discoveryCalls);
        public int ScanCalls => Volatile.Read(ref scanCalls);

        public static FakeScanAdapter WithPdf(
            IReadOnlyList<ScannerDevice> scanners,
            byte[] pdfBytes) =>
            WithDiscovery(new ScannerDiscoveryResult(scanners), pdfBytes);

        public static FakeScanAdapter WithDiscovery(
            ScannerDiscoveryResult discovery,
            byte[] pdfBytes) =>
            new(
                discovery,
                (_, _, _) => Task.FromResult<Stream>(
                    new MemoryStream(pdfBytes, writable: false)));

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref discoveryCalls);
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
            LastScannerId = scannerId;
            LastSource = source;
            Interlocked.Increment(ref scanCalls);
            return scanAsync(scannerId, source, cancellationToken);
        }
    }
}
