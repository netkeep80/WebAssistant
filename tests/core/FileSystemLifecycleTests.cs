using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAssistant.FileSystem;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemLifecycleTests : IDisposable
{
    private readonly string testRoot;
    private readonly string validRoot;
    private readonly string logDirectory;

    public FileSystemLifecycleTests()
    {
        testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-filesystem-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        validRoot = Path.Combine(testRoot, "data");
        logDirectory = Path.Combine(testRoot, "logs");
        Directory.CreateDirectory(validRoot);
        Directory.CreateDirectory(logDirectory);
    }

    [Fact]
    public void Provider_ValidRoot_IsAvailable()
    {
        using var provider = new RootedFileSystemProvider(validRoot);

        Assert.True(provider.IsAvailable);
        Assert.NotNull(provider.FileSystem);
    }

    [Fact]
    public void Provider_MissingOrLinkRoot_IsUnavailableWithoutFallback()
    {
        var missing = Path.Combine(testRoot, "missing");
        using (var missingProvider = new RootedFileSystemProvider(missing))
        {
            Assert.False(missingProvider.IsAvailable);
            Assert.Null(missingProvider.FileSystem);
        }

        var outside = Path.Combine(testRoot, "outside");
        var linkedRoot = Path.Combine(testRoot, "linked-root");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        using var linkedProvider = new RootedFileSystemProvider(linkedRoot);
        Assert.False(linkedProvider.IsAvailable);
        Assert.Null(linkedProvider.FileSystem);
    }

    [Fact]
    public async Task MissingRoot_ServiceStartsAndDiagnosticsDoNotDiscloseAbsoluteRoot()
    {
        var missing = Path.Combine(testRoot, "missing-service-root");
        using var factory = CreateFactory(missing);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/info");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"fileSystemState\":\"unavailable\"", json);
        Assert.DoesNotContain(missing, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRoot_DoesNotDisableScannerEndpoint()
    {
        var missing = Path.Combine(testRoot, "missing-scanner-root");
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Sane,
            "filesystem-lifecycle-scanner");
        var adapter = new FakeScanAdapter(
            [new ScannerDevice(scannerId, "Lifecycle scanner")]);
        using var factory = CreateFactory(missing, adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(scannerId, json);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string rootDirectory,
        IScanAdapter? adapter = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["WebAssistant:LogDirectory"] = logDirectory,
            ["WebAssistant:FileSystem:RootDirectory"] = rootDirectory
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));

            if (adapter is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IScanAdapter>();
                    services.AddSingleton(adapter);
                });
            }
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class FakeScanAdapter(
        IReadOnlyList<ScannerDevice> scanners) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScannerDiscoveryResult(scanners));
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(
                new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray(), writable: false));
        }
    }
}
