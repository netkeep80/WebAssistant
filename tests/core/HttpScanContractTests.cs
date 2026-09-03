using System.Net;
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
    public async Task Scanners_ReturnsIdAndName()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [
                new ScannerDevice("scanner-1", "Первый"),
                new ScannerDevice("scanner-2", "Второй")
            ],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scanner-1", json);
        Assert.Contains("Первый", json);
        Assert.Contains("scanner-2", json);
        Assert.Contains("Второй", json);
    }

    [Fact]
    public async Task Scan_WithOneScanner_ReturnsRawPdf()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Сканер")],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PdfBytes, body);
        Assert.Equal("scanner-1", adapter.LastScannerId);
        Assert.Equal(ScanSource.Glass, adapter.LastSource);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_WithNoScanners_ReturnsServiceUnavailableWithoutAcquisition()
    {
        var adapter = FakeScanAdapter.WithPdf([], PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_WithMultipleScanners_RequiresExplicitScannerId()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [
                new ScannerDevice("scanner-1", "Первый"),
                new ScannerDevice("scanner-2", "Второй")
            ],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
        Assert.Null(adapter.LastScannerId);
    }

    [Fact]
    public async Task Scan_ExplicitScanner_SelectsExactDevice()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [
                new ScannerDevice("scanner-1", "Первый"),
                new ScannerDevice("scanner-2", "Второй")
            ],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan?scannerId=scanner-2", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("scanner-2", adapter.LastScannerId);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_UnknownScanner_ReturnsNotFoundBeforeAcquisition()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Первый")],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan?scannerId=missing", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task Scan_EmptyExplicitScannerId_ReturnsBadRequestWithoutAcquisition()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Сканер")],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan?scannerId=", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, adapter.ScanCalls);
    }

    [Fact]
    public async Task ConcurrentScan_ReturnsBusyWithoutSecondAcquisition()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Сканер")],
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return new MemoryStream(PdfBytes, writable: false);
            });
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        var firstRequest = client.PostAsync("/v1/scan?scannerId=scanner-1", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var secondResponse = await client.PostAsync(
                "/v1/scan?scannerId=scanner-1",
                null);

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

    [Theory]
    [InlineData("/v1/scan", "Glass")]
    [InlineData("/v1/scan/feeder", "Feeder")]
    [InlineData("/v1/scan/duplex", "Duplex")]
    public async Task ScanEndpoints_MapExactRequestedSource(
        string endpoint,
        string expectedSourceName)
    {
        var expectedSource = Enum.Parse<ScanSource>(expectedSourceName);
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Сканер")],
            PdfBytes);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            endpoint + "?scannerId=scanner-1",
            null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedSource, adapter.LastSource);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Fact]
    public async Task AdapterFailure_DoesNotBecomeSuccessfulPdf()
    {
        var adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Сканер")],
            (_, _, _) => Task.FromException<Stream>(
                new InvalidOperationException("scanner failure")));
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EmptyPdf_DoesNotBecomeSuccessfulDocument()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Сканер")],
            []);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);

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
        IReadOnlyList<ScannerDevice> scanners,
        Func<string, ScanSource, CancellationToken, Task<Stream>> scanAsync) : IScanAdapter
    {
        private int scanCalls;

        public string? LastScannerId { get; private set; }
        public ScanSource? LastSource { get; private set; }
        public int ScanCalls => Volatile.Read(ref scanCalls);

        public static FakeScanAdapter WithPdf(
            IReadOnlyList<ScannerDevice> scanners,
            byte[] pdfBytes)
        {
            return new FakeScanAdapter(
                scanners,
                (_, _, _) => Task.FromResult<Stream>(
                    new MemoryStream(pdfBytes, writable: false)));
        }

        public Task<IReadOnlyList<ScannerDevice>> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(scanners);
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            return ScanAsync(scannerId, ScanSource.Glass, cancellationToken);
        }

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
