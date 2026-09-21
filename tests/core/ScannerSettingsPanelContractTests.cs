using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerSettingsPanelContractTests
{
    [Fact]
    public void ServicePanel_RendersScannerSettingsFromPublicCapabilitiesContract()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("id=\"scanner-mode\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-settings-controls\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-info\"", html, StringComparison.Ordinal);
        Assert.Contains("/v1/scanner-settings/schema", html, StringComparison.Ordinal);
        Assert.Contains("/settings", html, StringComparison.Ordinal);
        Assert.Contains("Object.entries(scannerSchema.fields", html, StringComparison.Ordinal);
        Assert.Contains("settingControlId(fieldName)", html, StringComparison.Ordinal);
        Assert.Contains("select.dataset.settingName=fieldName", html, StringComparison.Ordinal);

        Assert.DoesNotContain("id=\"scan-feeder\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"scan-duplex\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicePanel_HasOneScanActionAndCapabilityDrivenModeSelector()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("id=\"scan-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-mode\"", html, StringComparison.Ordinal);
        Assert.Contains("Информация о сканере", html, StringComparison.Ordinal);
        Assert.Contains("for(const mode of scannerProjection.modes", html, StringComparison.Ordinal);
        Assert.Contains("source:mode.source", html, StringComparison.Ordinal);
        Assert.Contains("duplex:Boolean(mode.duplex)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicePanel_ProbesCapabilitiesOnlyAfterExplicitScannerSelection()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("Выберите сканер", html, StringComparison.Ordinal);
        Assert.Contains(
            "scannerSelect.addEventListener(\"change\",refreshSelectedScannerSettings)",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("await refreshSelectedScannerSettings();", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_ClearsPreviousPdfWhenScannerContextChangesOrNewScanFails()
    {
        await RunPanelScenarioAsync(async (page, baseUrl) =>
        {
            var scanCalls = 0;
            await page.RouteAsync("**/v1/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                switch (path)
                {
                    case "/v1/diag/info":
                        await FulfillJsonAsync(route, """
                            {"version":"test","os":"test","listenUrl":"http://127.0.0.1","apiVersion":"v1","scanState":"idle"}
                            """);
                        return;
                    case "/v1/scanner-settings/schema":
                        await FulfillJsonAsync(route, """
                            {"fields":{"dpi":{"title":"DPI","type":"integer"}}}
                            """);
                        return;
                    case "/v1/scanners":
                        await FulfillJsonAsync(route, """
                            {"scanners":[{"scannerId":"scanner-a","name":"Scanner A","backend":"wia"},{"scannerId":"scanner-b","name":"Scanner B","backend":"twain"}],"warnings":[]}
                            """);
                        return;
                    case "/v1/scanners/scanner-a/settings":
                        await FulfillJsonAsync(route, Projection("scanner-a", "flatbed", "flatbed"));
                        return;
                    case "/v1/scanners/scanner-b/settings":
                        await FulfillJsonAsync(route, """{"title":"capability failed"}""", 502);
                        return;
                    case "/v1/scan":
                        scanCalls++;
                        if (scanCalls == 1)
                        {
                            await route.FulfillAsync(new RouteFulfillOptions
                            {
                                Status = 200,
                                ContentType = "application/pdf",
                                Body = "%PDF-1.7\n%%EOF"
                            });
                        }
                        else
                        {
                            await FulfillJsonAsync(route, """{"title":"scan failed"}""", 500);
                        }
                        return;
                    default:
                        await route.AbortAsync();
                        return;
                }
            });

            await page.GotoAsync(baseUrl + "/");
            await page.WaitForFunctionAsync("!document.getElementById('scanner-select').disabled");

            await page.SelectOptionAsync("#scanner-select", "scanner-a");
            await page.WaitForFunctionAsync("!document.getElementById('scan-button').disabled");
            await page.ClickAsync("#scan-button");
            await page.Locator("#pdf-result").WaitForAsync(new() { State = WaitForSelectorState.Visible });
            Assert.True(await page.Locator("#pdf-result").IsVisibleAsync());

            await page.SelectOptionAsync("#scanner-select", "scanner-b");
            await page.WaitForFunctionAsync(
                "document.getElementById('scanner-status').textContent.includes('Ошибка capabilities')");
            Assert.True(await page.Locator("#pdf-result").IsHiddenAsync());
            Assert.Equal(string.Empty, (await page.Locator("#scan-status").InnerTextAsync()).Trim());

            await page.SelectOptionAsync("#scanner-select", "scanner-a");
            await page.WaitForFunctionAsync("!document.getElementById('scan-button').disabled");
            await page.ClickAsync("#scan-button");
            await page.WaitForFunctionAsync(
                "document.getElementById('scan-status').textContent.includes('Ошибка:')");
            Assert.True(await page.Locator("#pdf-result").IsHiddenAsync());
        });
    }

    [Fact]
    public async Task Browser_DoesNotEmbedPdfWhenInlineViewerIsUnavailable()
    {
        await RunPanelScenarioAsync(async (page, baseUrl) =>
        {
            await page.AddInitScriptAsync("""
                Object.defineProperty(Navigator.prototype, "pdfViewerEnabled", {
                  configurable: true,
                  get: () => false
                });
                """);

            await page.RouteAsync("**/v1/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                switch (path)
                {
                    case "/v1/diag/info":
                        await FulfillJsonAsync(route, """
                            {"version":"test","os":"test","listenUrl":"http://127.0.0.1","apiVersion":"v1","scanState":"idle"}
                            """);
                        return;
                    case "/v1/scanner-settings/schema":
                        await FulfillJsonAsync(route, """
                            {"fields":{"dpi":{"title":"DPI","type":"integer"}}}
                            """);
                        return;
                    case "/v1/scanners":
                        await FulfillJsonAsync(route, """
                            {"scanners":[{"scannerId":"scanner-a","name":"Scanner A","backend":"wia"}],"warnings":[]}
                            """);
                        return;
                    case "/v1/scanners/scanner-a/settings":
                        await FulfillJsonAsync(route, Projection("scanner-a", "flatbed", "flatbed"));
                        return;
                    case "/v1/scan":
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "application/pdf",
                            Body = "%PDF-1.7\n%%EOF"
                        });
                        return;
                    default:
                        await route.AbortAsync();
                        return;
                }
            });

            await page.GotoAsync(baseUrl + "/");
            await page.WaitForFunctionAsync("!document.getElementById('scanner-select').disabled");

            await page.SelectOptionAsync("#scanner-select", "scanner-a");
            await page.WaitForFunctionAsync("!document.getElementById('scan-button').disabled");
            await page.ClickAsync("#scan-button");
            await page.Locator("#pdf-result").WaitForAsync(new() { State = WaitForSelectorState.Visible });

            Assert.StartsWith(
                "blob:",
                await page.Locator("#pdf-download").GetAttributeAsync("href") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.StartsWith(
                "blob:",
                await page.Locator("#pdf-open").GetAttributeAsync("href") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.True(await page.Locator("#pdf-preview").IsHiddenAsync());
            Assert.True(string.IsNullOrEmpty(
                await page.Locator("#pdf-preview").GetAttributeAsync("data")));
            Assert.Contains(
                "Встроенный просмотр PDF недоступен",
                await page.Locator("#pdf-preview-status").InnerTextAsync(),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Browser_IgnoresLateCapabilitiesResponseForPreviouslySelectedScanner()
    {
        await RunPanelScenarioAsync(async (page, baseUrl) =>
        {
            var heldScannerA = new TaskCompletionSource<IRoute>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await page.RouteAsync("**/v1/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                switch (path)
                {
                    case "/v1/diag/info":
                        await FulfillJsonAsync(route, """
                            {"version":"test","os":"test","listenUrl":"http://127.0.0.1","apiVersion":"v1","scanState":"idle"}
                            """);
                        return;
                    case "/v1/scanner-settings/schema":
                        await FulfillJsonAsync(route, """
                            {"fields":{"dpi":{"title":"DPI","type":"integer"}}}
                            """);
                        return;
                    case "/v1/scanners":
                        await FulfillJsonAsync(route, """
                            {"scanners":[{"scannerId":"scanner-a","name":"Scanner A","backend":"wia"},{"scannerId":"scanner-b","name":"Scanner B","backend":"twain"}],"warnings":[]}
                            """);
                        return;
                    case "/v1/scanners/scanner-a/settings":
                        heldScannerA.TrySetResult(route);
                        return;
                    case "/v1/scanners/scanner-b/settings":
                        await FulfillJsonAsync(route, Projection("scanner-b", "feeder", "feeder"));
                        return;
                    default:
                        await route.AbortAsync();
                        return;
                }
            });

            await page.GotoAsync(baseUrl + "/");
            await page.WaitForFunctionAsync("!document.getElementById('scanner-select').disabled");

            await page.SelectOptionAsync("#scanner-select", "scanner-a");
            var scannerARoute = await heldScannerA.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await page.SelectOptionAsync("#scanner-select", "scanner-b");
            await page.WaitForFunctionAsync("document.getElementById('scanner-mode').value === 'feeder'");

            await FulfillJsonAsync(scannerARoute, Projection("scanner-a", "flatbed", "flatbed"));
            await page.EvaluateAsync("() => new Promise(resolve => setTimeout(resolve, 0))");
            await page.EvaluateAsync("() => new Promise(resolve => setTimeout(resolve, 0))");

            Assert.Equal("scanner-b", await page.Locator("#scanner-select").InputValueAsync());
            Assert.Equal("feeder", await page.Locator("#scanner-mode").InputValueAsync());
        });
    }

    private static string Projection(string scannerId, string mode, string source) =>
        JsonSerializer.Serialize(new
        {
            scannerId,
            modes = new[]
            {
                new
                {
                    mode,
                    source,
                    duplex = false,
                    settings = new
                    {
                        dpi = new
                        {
                            supported = true,
                            values = new[] { 300 },
                            @default = 300
                        }
                    }
                }
            }
        });

    private static Task FulfillJsonAsync(IRoute route, string body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = body
        });

    private static async Task RunPanelScenarioAsync(Func<IPage, string, Task> scenario)
    {
        var root = FindRepositoryRoot();
        var temp = Path.Combine(Path.GetTempPath(), "webassistant-scanner-panel", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(temp, "logs");
        Directory.CreateDirectory(logs);
        var port = FreePort();
        var product = Path.Combine(root, "webassist", "src", "WebAssistant");
        var psi = new ProcessStartInfo(
            "dotnet",
            $"run --no-build --project \"{Path.Combine(product, "WebAssistant.csproj")}\" --configuration Release")
        {
            UseShellExecute = false,
            WorkingDirectory = product
        };
        psi.Environment["WebAssistant__Port"] = port.ToString();
        psi.Environment["WebAssistant__LogDirectory"] = logs;
        using var service = Process.Start(psi)!;

        try
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            await WaitHealthyAsync(baseUrl);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await scenario(page, baseUrl);
        }
        finally
        {
            if (!service.HasExited)
            {
                service.Kill(entireProcessTree: true);
            }
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitHealthyAsync(string baseUrl)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if ((await client.GetAsync(baseUrl + "/v1/health")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("WebAssistant scanner panel test server did not start.");
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

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}
