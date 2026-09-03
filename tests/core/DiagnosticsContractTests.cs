using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DiagnosticsContractTests
{
    private static readonly byte[] SecretPdfBytes =
        "%PDF-1.7\nDOCUMENT-SECRET-MARKER\n%%EOF"u8.ToArray();

    [Fact]
    public async Task DiagnosticsInfo_ReturnsCurrentSafeRuntimeState()
    {
        using var fixture = CreateFixture();
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/info");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"apiVersion\":\"v1\"", json);
        Assert.Contains("http://127.0.0.1:17654", json);
        Assert.Contains("\"scanState\":\"idle\"", json);
    }

    [Fact]
    public async Task DiagnosticsLogs_RejectsInvalidDateInsteadOfAcceptingPath()
    {
        using var fixture = CreateFixture();
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync(
            "/v1/diag/logs?date=../../etc/passwd");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DiagnosticsLogs_ReturnsOnlyOwnDailyLog()
    {
        using var fixture = CreateFixture();
        var date = DateOnly.FromDateTime(DateTime.Now);
        var expected = "2026-09-01T12:00:00+00:00 [Information] WebAssistant.Test Проверка журнала\n";
        var file = Path.Combine(
            fixture.LogDirectory,
            $"webassistant-{date:yyyy-MM-dd}.log");
        await File.WriteAllTextAsync(file, expected);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v1/diag/logs?date={date:yyyy-MM-dd}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task Scan_WritesDailyTechnicalLogWithoutDocumentContent()
    {
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice("scanner-1", "Сканер")],
            SecretPdfBytes);
        using var fixture = CreateFixture(adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains("POST /v1/scan", log);
        Assert.Contains("scanner-1", log);
        Assert.DoesNotContain("DOCUMENT-SECRET-MARKER", log);
        Assert.DoesNotContain(Convert.ToBase64String(SecretPdfBytes), log);
    }

    [Fact]
    public async Task ScannerFailure_WritesExceptionTypeMessageAndStackTrace()
    {
        var adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Сканер")],
            (_, _) => Task.FromException<Stream>(
                new InvalidOperationException("scanner failure")));
        using var fixture = CreateFixture(adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsync("/v1/scan", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains("InvalidOperationException", log);
        Assert.Contains("scanner failure", log);
        Assert.Contains("scanner-1", log);
    }

    private static async Task<string> ReadTodayLogAsync(string logDirectory)
    {
        var file = Path.Combine(
            logDirectory,
            $"webassistant-{DateTimeOffset.Now:yyyy-MM-dd}.log");

        Assert.True(File.Exists(file), $"Ожидался файл журнала {file}");
        return await File.ReadAllTextAsync(file);
    }

    private static TestFixture CreateFixture(IScanAdapter? adapter = null)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-tests",
            Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(testRoot, "logs");
        var fileSystemRoot = Path.Combine(testRoot, "data");
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(fileSystemRoot);

        var settings = new Dictionary<string, string?>
        {
            ["WebAssistant:LogDirectory"] = logDirectory,
            ["WebAssistant:FileSystem:RootDirectory"] = fileSystemRoot
        };

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(settings);
            });

            if (adapter is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IScanAdapter>();
                    services.AddSingleton(adapter);
                });
            }
        });

        return new TestFixture(factory, testRoot, logDirectory);
    }

    private sealed class TestFixture(
        WebApplicationFactory<Program> factory,
        string testRoot,
        string logDirectory) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;

        public string LogDirectory { get; } = logDirectory;

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
            cancellationToken.ThrowIfCancellationRequested();
            return scanAsync(scannerId, cancellationToken);
        }
    }
}
