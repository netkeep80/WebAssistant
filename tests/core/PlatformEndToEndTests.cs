using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class PlatformEndToEndTests
{
    private const string BrowserBaseUrl = "http://127.0.0.1:17654/";

    [Fact]
    public async Task ServicePanel_IsServedAtRoot()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("WebAssistant", body);
    }

    [Fact]
    public async Task ServicePanel_UsesCurrentVersionedApi()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/");

        Assert.Contains("/v1/diag/info", body);
        Assert.Contains("/v1/scanners", body);
        Assert.Contains("/v1/scan", body);
        Assert.Contains("/v1/diag/logs", body);
    }

    [Fact]
    public async Task ServicePanel_ExposesPdfBlobForPreviewAndSave()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/");

        Assert.Contains("URL.createObjectURL", body);
        Assert.Contains("URL.revokeObjectURL", body);
        Assert.Contains("id=\"pdf-preview\"", body);
        Assert.Contains("id=\"pdf-download\"", body);
    }

    [Fact]
    public async Task ServicePanel_ExposesMachineReadableServiceState()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/");

        Assert.Contains("serviceState.dataset.state=\"ok\"", body);
        Assert.Contains("serviceState.dataset.state=\"error\"", body);
    }

    [Fact]
    [Trait("Category", "PlatformVirtualEndToEnd")]
    public async Task BrowserFacingFlow_UsesRealPlatformAdapterAndRecoversAfterRepeat()
    {
        if (!ShouldRunPlatformEndToEnd())
        {
            return;
        }

        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var scannerId = await GetFirstScannerIdAsync(client);
        var scanPath = "/v1/scan?scannerId=" + Uri.EscapeDataString(scannerId);

        using var first = await client.PostAsync(scanPath, null);
        await AssertPdfAsync(first);

        using var repeat = await client.PostAsync(scanPath, null);
        await AssertPdfAsync(repeat);
    }

    [Fact]
    [Trait("Category", "PlatformVirtualEndToEnd")]
    public async Task ServicePanel_BrowserFlow_UsesRealPlatformAdapterAndPdfBlob()
    {
        if (!ShouldRunPlatformEndToEnd())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"webassistant-browser-e2e-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(logDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        var processOutput = new StringBuilder();
        using var agent = StartAgent(repositoryRoot, logDirectory, dataDirectory, processOutput);

        try
        {
            await WaitForAgentAsync(agent, processOutput);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Channel = "chrome",
                    Headless = true,
                    Args = OperatingSystem.IsLinux() ? ["--no-sandbox"] : []
                });

            var page = await browser.NewPageAsync();
            var observedRequests = new ConcurrentBag<string>();
            page.Request += (_, request) => observedRequests.Add(request.Url);

            var navigation = await page.GotoAsync(
                BrowserBaseUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });

            Assert.NotNull(navigation);
            Assert.True(navigation!.Ok, $"GET / завершился HTTP {navigation.Status}.");

            await page
                .Locator("#service-state[data-state='ok']")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page
                .Locator("#scanner-select:not([disabled])")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            var scannerId = await page.Locator("#scanner-select").InputValueAsync();
            Assert.False(string.IsNullOrWhiteSpace(scannerId));

            var scanResponseTask = page.WaitForResponseAsync(
                response =>
                    response.Request.Method == "POST" &&
                    response.Url.StartsWith(
                        BrowserBaseUrl + "v1/scan?scannerId=",
                        StringComparison.Ordinal),
                new PageWaitForResponseOptions { Timeout = 90_000 });

            await page.Locator("#scan-button").ClickAsync();
            var scanResponse = await scanResponseTask;

            Assert.True(
                scanResponse.Ok,
                $"Сканирование из браузера завершилось HTTP {scanResponse.Status}.");
            Assert.True(
                scanResponse.Headers.TryGetValue("content-type", out var contentType));
            Assert.StartsWith("application/pdf", contentType, StringComparison.OrdinalIgnoreCase);

            await page
                .Locator("#pdf-preview:not([hidden])")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page
                .Locator("#pdf-download:not([hidden])")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            var previewUrl = await page
                .Locator("#pdf-preview")
                .GetAttributeAsync("data");
            var downloadUrl = await page
                .Locator("#pdf-download")
                .GetAttributeAsync("href");

            Assert.StartsWith("blob:", previewUrl);
            Assert.StartsWith("blob:", downloadUrl);
            Assert.Equal(previewUrl, downloadUrl);

            var blobSize = await page.EvaluateAsync<int>(
                "async url => (await (await fetch(url)).arrayBuffer()).byteLength",
                previewUrl);
            var blobSignature = await page.EvaluateAsync<string>(
                "async url => { const bytes = new Uint8Array(await (await fetch(url)).arrayBuffer()); return String.fromCharCode(...bytes.slice(0, 5)); }",
                previewUrl);

            Assert.True(blobSize > 5);
            Assert.Equal("%PDF-", blobSignature);

            await page
                .Locator("#service-state[data-state='ok']")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            Assert.Contains(observedRequests, url => url == BrowserBaseUrl + "v1/diag/info");
            Assert.Contains(observedRequests, url => url == BrowserBaseUrl + "v1/scanners");
            Assert.Contains(
                observedRequests,
                url => url.StartsWith(
                    BrowserBaseUrl + "v1/scan?scannerId=",
                    StringComparison.Ordinal));
        }
        finally
        {
            await StopAgentAsync(agent);
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    private static async Task<string> GetFirstScannerIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/v1/scanners");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var scanners = document.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(scanners);

        var scannerId = scanners[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(scannerId));
        return scannerId!;
    }

    private static bool ShouldRunPlatformEndToEnd()
    {
        return Environment.GetEnvironmentVariable("WEBASSISTANT_PLATFORM_E2E") == "1" &&
            (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
    }

    private static Process StartAgent(
        string repositoryRoot,
        string logDirectory,
        string dataDirectory,
        StringBuilder processOutput)
    {
        var projectDirectory = Path.Combine(
            repositoryRoot,
            "webassist",
            "src",
            "WebAssistant");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build --configuration Release",
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["WebAssistant__LogDirectory"] = logDirectory;
        startInfo.Environment["WebAssistant__FileSystem__RootDirectory"] = dataDirectory;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) => AppendProcessLine(processOutput, args.Data);
        process.ErrorDataReceived += (_, args) => AppendProcessLine(processOutput, args.Data);

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Не удалось запустить WebAssistant для browser E2E.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForAgentAsync(
        Process process,
        StringBuilder processOutput)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(BrowserBaseUrl),
            Timeout = TimeSpan.FromSeconds(2)
        };

        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"WebAssistant завершился до готовности, exit={process.ExitCode}.\n{ReadProcessOutput(processOutput)}");
            }

            try
            {
                using var response = await client.GetAsync("v1/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"WebAssistant не стал готов за 30 секунд.\n{ReadProcessOutput(processOutput)}");
    }

    private static async Task StopAgentAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static void AppendProcessLine(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static string ReadProcessOutput(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "webassist",
                    "src",
                    "WebAssistant",
                    "WebAssistant.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Не найден корень репозитория WebAssistant для browser E2E.");
    }

    private static async Task AssertPdfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/pdf",
            response.Content.Headers.ContentType?.MediaType);
        Assert.True(body.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(body, 0, 5));
    }
}
