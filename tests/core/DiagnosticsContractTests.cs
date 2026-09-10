using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WebAssistant.Runtime;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DiagnosticsContractTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%%EOF"u8.ToArray();

    [Fact]
    public async Task Info_ReturnsVersionOsUptimeAndScanState()
    {
        using var context = TestContext.Create(FakeScanAdapter.WithPdf([], PdfBytes));
        using var client = context.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/info");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("version", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("os", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uptimeSeconds", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("listenUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apiVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scanState", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idle", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Info_ReflectsBusyScannerState()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Сканер")],
            async (_, cancellationToken) =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return new MemoryStream(PdfBytes, writable: false);
            });
        using var context = TestContext.Create(adapter);
        using var client = context.Factory.CreateClient();

        var scan = client.PostAsync("/v1/scan?scannerId=scanner-1", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var json = await client.GetStringAsync("/v1/diag/info");
            Assert.Contains("busy", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.TrySetResult(true);
        }

        using var scanResponse = await scan;
        Assert.Equal(HttpStatusCode.OK, scanResponse.StatusCode);
    }

    [Fact]
    public async Task Logs_RequireExactDateAndReturnOnlyOwnDailyLog()
    {
        using var context = TestContext.Create(FakeScanAdapter.WithPdf([], PdfBytes));
        var options = context.Factory.Services.GetRequiredService<WebAssistantRuntimeOptions>();
        Directory.CreateDirectory(options.LogDirectory);
        var date = DateOnly.FromDateTime(DateTime.Today);
        var expected = "safe log line\n";
        await File.WriteAllTextAsync(
            Path.Combine(options.LogDirectory, $"webassistant-{date:yyyy-MM-dd}.log"),
            expected);

        using var client = context.Factory.CreateClient();
        using var response = await client.GetAsync($"/v1/diag/logs?date={date:yyyy-MM-dd}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-02-30")]
    [InlineData("../../secret")]
    public async Task Logs_InvalidDate_IsRejected(string date)
    {
        using var context = TestContext.Create(FakeScanAdapter.WithPdf([], PdfBytes));
        using var client = context.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/logs?date=" + Uri.EscapeDataString(date));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logs_MissingOwnLog_ReturnsNotFound()
    {
        using var context = TestContext.Create(FakeScanAdapter.WithPdf([], PdfBytes));
        using var client = context.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/logs?date=1999-01-01");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void AgentRuntimeInfo_LoadsVersionFromProductVersionFile()
    {
        var info = new AgentRuntimeInfo();
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", info.Version);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string testRoot;

        private TestContext(string testRoot, WebApplicationFactory<Program> factory)
        {
            this.testRoot = testRoot;
            Factory = factory;
        }

        public WebApplicationFactory<Program> Factory { get; }

        public static TestContext Create(IScanAdapter adapter)
        {
            var testRoot = Path.Combine(Path.GetTempPath(), $"webassistant-diag-{Guid.NewGuid():N}");
            var logDirectory = Path.Combine(testRoot, "logs");
            var dataDirectory = Path.Combine(testRoot, "data");
            Directory.CreateDirectory(logDirectory);
            Directory.CreateDirectory(dataDirectory);

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("WebAssistant:LogDirectory", logDirectory);
                builder.UseSetting("WebAssistant:FileSystem:RootDirectory", dataDirectory);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IScanAdapter>();
                    services.AddSingleton(adapter);
                    services.RemoveAll<ILoggerProvider>();
                });
            });

            return new TestContext(testRoot, factory);
        }

        public void Dispose()
        {
            Factory.Dispose();
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeScanAdapter(
        IReadOnlyList<ScannerDevice> scanners,
        Func<string, CancellationToken, Task<Stream>> scanAsync) : IScanAdapter
    {
        public static FakeScanAdapter WithPdf(
            IReadOnlyList<ScannerDevice> scanners,
            byte[] pdfBytes)
        {
            return new FakeScanAdapter(
                scanners,
                (_, _) => Task.FromResult<Stream>(
                    new MemoryStream(pdfBytes, writable: false)));
        }

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
            return scanAsync(scannerId, cancellationToken);
        }
    }
}
