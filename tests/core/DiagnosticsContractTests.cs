using System.Net;
using System.Net.Http.Json;
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
    private static readonly byte[] DocumentPdfBytes =
        "%PDF-1.7\nDOCUMENT-CONTENT-MARKER\n%%EOF"u8.ToArray();

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
        Assert.Contains("\"fileSystemState\":\"available\"", json);
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
        var scannerId = ScannerIdentity.Create(ScannerBackend.Sane, "diagnostics-scanner-1");
        var adapter = FakeScanAdapter.WithPdf(
            [new ScannerDevice(scannerId, "Сканер")],
            DocumentPdfBytes);
        using var fixture = CreateFixture(adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/scan",
            new { scannerId, source = "flatbed" });
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains("POST /v1/scan", log);
        Assert.Contains(scannerId, log);
        Assert.DoesNotContain("DOCUMENT-CONTENT-MARKER", log);
        Assert.DoesNotContain(Convert.ToBase64String(DocumentPdfBytes), log);
    }

    [Fact]
    public async Task FileSystem_LogsLogicalAuthorityWithoutPhysicalPathOrContent()
    {
        using var fixture = CreateFixture();
        using var client = fixture.Factory.CreateClient();
        var marker = $"private-{Guid.NewGuid():N}.bin";
        var moved = $"moved-{Guid.NewGuid():N}.bin";
        var content = $"secret-{Guid.NewGuid():N}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var source = $"archive/{marker}";
        var destination = $"archive/{moved}";

        using (var upload = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/filesystem/file?path={Uri.EscapeDataString(source)}")
        {
            Content = new ByteArrayContent(bytes)
        })
        {
            upload.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.SendAsync(upload)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync(
                "/v1/filesystem/rename",
                new { path = source, newName = moved })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync(
                $"/v1/filesystem/file/delete?path={Uri.EscapeDataString(destination)}",
                content: null)).StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains("logicalRoot=archive", log);
        Assert.Contains($"relativePath={marker}", log);
        Assert.Contains($"relativePath={moved}", log);
        Assert.DoesNotContain(content, log);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), log);
        Assert.DoesNotContain(fixture.FileSystemRoot, log);
    }

    [Fact]
    public async Task ScannerFailure_WritesSafeExceptionMetadataWithoutRawMessageOrStack()
    {
        var scannerId = ScannerIdentity.Create(ScannerBackend.Sane, "diagnostics-scanner-1");
        var adapter = new FakeScanAdapter(
            [new ScannerDevice(scannerId, "Сканер")],
            (_, _) => Task.FromException<Stream>(
                new InvalidOperationException("scanner failure")));
        using var fixture = CreateFixture(adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/scan",
            new { scannerId, source = "flatbed" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains("exceptionType=InvalidOperationException", log);
        Assert.Contains("hresult=0x", log);
        Assert.Contains(scannerId, log);
        Assert.DoesNotContain("scanner failure", log);
        Assert.DoesNotContain("System.InvalidOperationException:", log);
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
            ["WebAssistant:FileSystem:archive"] = fileSystemRoot
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
        public string FileSystemRoot => Path.Combine(testRoot, "data");

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

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ScannerDevice?>(scanners.FirstOrDefault(scanner =>
                string.Equals(scanner.Id, scannerId, StringComparison.Ordinal)));
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
