using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemBrowserUploadDiagnosticsTests
{
    [Fact]
    public async Task Browser_UploadRefreshUsesCommittedListing()
    {
        var repo = FindRoot();
        var temp = Path.Combine(Path.GetTempPath(), "webassistant-upload-probe", Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(temp, "archive");
        var logs = Path.Combine(temp, "logs");
        Directory.CreateDirectory(Path.Combine(archive, "incoming"));
        Directory.CreateDirectory(logs);
        var port = FreePort();
        var product = Path.Combine(repo, "webassist", "src", "WebAssistant");
        var psi = new ProcessStartInfo(
            "dotnet",
            $"run --no-build --project \"{Path.Combine(product, "WebAssistant.csproj")}\" --configuration Release")
        {
            UseShellExecute = false,
            WorkingDirectory = product
        };
        psi.Environment["WebAssistant__Port"] = port.ToString();
        psi.Environment["WebAssistant__FileSystem__archive"] = archive;
        psi.Environment["WebAssistant__LogDirectory"] = logs;
        using var service = Process.Start(psi)!;

        try
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            await WaitHealthy(baseUrl);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var pageErrors = new List<string>();
            page.PageError += (_, error) => pageErrors.Add(error);
            await page.GotoAsync(baseUrl + "/filesystem.html");

            await page.Locator("#filesystem-roots [data-root='archive'][aria-pressed='true']").WaitForAsync();
            await page.Locator("#filesystem-left-status").GetByText("Объектов:", new() { Exact = false }).WaitForAsync();
            await page.Locator("#filesystem-right-status").GetByText("Объектов:", new() { Exact = false }).WaitForAsync();
            var incoming = page.Locator("#filesystem-left-entries tr").Filter(new() { HasTextString = "incoming" });
            await incoming.Locator("[data-entry-open]").ClickAsync();
            await page.Locator("#filesystem-left-breadcrumb [data-path='archive/incoming']").WaitForAsync();
            await page.Locator("#filesystem-left-status").GetByText("Объектов: 0", new() { Exact = true }).WaitForAsync();

            var upload = Path.Combine(temp, "a.bin");
            await File.WriteAllTextAsync(upload, "browser-upload-probe");
            var putSource = new TaskCompletionSource<IResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listSource = new TaskCompletionSource<IResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<IResponse> observe = (_, response) =>
            {
                if (response.Request.Method == "PUT" && response.Url.Contains("/v1/filesystem/file", StringComparison.Ordinal))
                {
                    putSource.TrySetResult(response);
                }
                else if (response.Request.Method == "GET" &&
                         response.Url.Contains("/v1/filesystem/list", StringComparison.Ordinal) &&
                         Uri.UnescapeDataString(response.Url).Contains("path=archive/incoming", StringComparison.Ordinal))
                {
                    listSource.TrySetResult(response);
                }
            };
            page.Response += observe;
            var chooser = await page.RunAndWaitForFileChooserAsync(() => page.ClickAsync("#filesystem-left-upload"));
            await chooser.SetFilesAsync(upload);

            var put = await putSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(put.Ok, $"PUT HTTP {put.Status}");
            var list = await listSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await list.FinishedAsync();
            var listBody = await list.TextAsync();
            page.Response -= observe;

            Assert.Contains("\"name\":\"a.bin\"", listBody, StringComparison.Ordinal);
            await page.WaitForFunctionAsync("!document.getElementById('filesystem-left-refresh').disabled");
            var entries = await page.Locator("#filesystem-left-entries").InnerTextAsync();
            var status = await page.Locator("#filesystem-left-status").InnerTextAsync();
            Assert.True(
                entries.Contains("a.bin", StringComparison.Ordinal),
                $"Committed list contains a.bin but DOM does not. url={list.Url}; status={status}; errors={string.Join(" | ", pageErrors)}; body={listBody}; entries={entries}");
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

    private static async Task WaitHealthy(string url)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if ((await client.GetAsync(url + "/v1/health")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("WebAssistant browser upload probe server did not start.");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException();
    }
}
