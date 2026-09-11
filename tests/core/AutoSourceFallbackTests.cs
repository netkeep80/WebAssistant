using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NAPS2.Scan.Exceptions;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class AutoSourceFallbackTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%%EOF"u8.ToArray();

    [Fact]
    public async Task Auto_FeederReportedPresentButPhysicallyEmpty_RetriesFlatbedOnce()
    {
        var scanner = CreateDualSourceScanner();
        var adapter = new FeederEmptyAdapter(scanner);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = scanner.Id,
            source = "auto"
        });
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PdfBytes, body);
        Assert.Equal(new[] { ScanSource.Feeder, ScanSource.Glass }, adapter.Sources);
    }

    [Fact]
    public async Task ExplicitFeeder_PhysicallyEmpty_DoesNotFallBackToFlatbed()
    {
        var scanner = CreateDualSourceScanner();
        var adapter = new FeederEmptyAdapter(scanner);
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = scanner.Id,
            source = "feeder"
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(new[] { ScanSource.Feeder }, adapter.Sources);
    }

    private static ScannerDevice CreateDualSourceScanner() =>
        new(
            ScannerIdentity.Create(ScannerBackend.Wia, "physical-samsung-wia"),
            "Samsung Scanner Class Driver",
            ScannerBackend.Wia,
            SupportsFlatbed: true,
            SupportsFeeder: true,
            SupportsDuplex: false,
            FeederPaperState.Present);

    private static WebApplicationFactory<Program> CreateFactory(IScanAdapter adapter) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanAdapter>();
                services.AddSingleton(adapter);
            });
        });

    private sealed class FeederEmptyAdapter(ScannerDevice scanner) : IScanAdapter
    {
        private readonly List<ScanSource> sources = [];

        internal IReadOnlyList<ScanSource> Sources => sources;

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScannerDiscoveryResult([scanner]));
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
            Assert.Equal(scanner.Id, scannerId);
            sources.Add(source);

            return source == ScanSource.Feeder
                ? Task.FromException<Stream>(new DeviceFeederEmptyException())
                : Task.FromResult<Stream>(new MemoryStream(PdfBytes, writable: false));
        }
    }
}

#pragma warning restore CA2252
