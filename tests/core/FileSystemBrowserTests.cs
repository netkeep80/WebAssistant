using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemBrowserTests
{
    [Fact]
    public async Task Browser_Exercises230TwoPanelSelectionAndSeparatedMutations()
    {
        var repo = FindRoot();
        var temp = Path.Combine(
            Path.GetTempPath(),
            "webassistant-browser-v230",
            Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(temp, "archive");
        var nfs = Path.Combine(temp, "nfs");
        var offline = Path.Combine(temp, "offline");
        var logs = Path.Combine(temp, "logs");
        var incoming = Path.Combine(archive, "incoming");
        var processed = Path.Combine(archive, "processed");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(processed);
        Directory.CreateDirectory(Path.Combine(incoming, "nested"));
        Directory.CreateDirectory(nfs);
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(incoming, "a.xml"), "a");
        await File.WriteAllTextAsync(Path.Combine(incoming, "b.xml"), "b");
        await File.WriteAllTextAsync(Path.Combine(incoming, "c.json"), "c");
        await File.WriteAllTextAsync(Path.Combine(archive, "blocked.sh"), "echo blocked");

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
        psi.Environment["WebAssistant__FileSystem__nfs"] = nfs;
        psi.Environment["WebAssistant__FileSystem__offline"] = offline;
        psi.Environment["WebAssistant__LogDirectory"] = logs;
        using var service = Process.Start(psi)!;

        try
        {
            var baseUrl = $"http://127.0.0.1:{port}";
            await WaitHealthy(baseUrl);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var prompts = new Queue<string>();
            var pageErrors = new List<string>();
            var filesystemRequests = new List<(string Method, string Url, string? PostData)>();
            page.PageError += (_, error) => pageErrors.Add(error);
            page.Request += (_, request) =>
            {
                if (request.Url.Contains("/v1/filesystem/", StringComparison.Ordinal))
                {
                    filesystemRequests.Add((request.Method, request.Url, request.PostData));
                }
            };
            page.Dialog += async (_, dialog) =>
            {
                if (dialog.Type == "prompt")
                {
                    await dialog.AcceptAsync(prompts.Dequeue());
                }
                else
                {
                    await dialog.AcceptAsync();
                }
            };

            await page.GotoAsync(baseUrl + "/filesystem.html");

            ILocator Entries(string side) => page.Locator($"#filesystem-{side}-entries");
            ILocator Row(string side, string name) => Entries(side)
                .Locator($"tr[data-entry-name='{name}']");
            async Task Visible(string side, string name) => await Row(side, name).WaitForAsync();
            async Task WaitBreadcrumb(string side, string expected) =>
                await page.WaitForFunctionAsync(
                    "args => document.getElementById(args.id).innerText.includes(args.expected)",
                    new { id = $"filesystem-{side}-breadcrumb", expected });
            async Task Prompt(string selector, string value)
            {
                prompts.Enqueue(value);
                await page.ClickAsync(selector);
            }
            async Task<int> SelectedCount(string side) =>
                await Entries(side).Locator("tr.selected-row").CountAsync();

            await page.Locator("#filesystem-roots [data-root='archive']").WaitForAsync();
            await page.Locator("#filesystem-roots [data-root='nfs']").WaitForAsync();
            await page.Locator("#filesystem-roots [data-root='offline']").WaitForAsync();
            await page.ClickAsync("#filesystem-roots [data-root='archive']");
            await WaitBreadcrumb("left", "archive");
            await WaitBreadcrumb("right", "archive");

            await Row("left", "incoming").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("left", "archive / incoming");
            await Row("right", "processed").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("right", "archive / processed");

            Assert.Equal("*.*", await page.Locator("#filesystem-left-wildcard").InputValueAsync());
            Assert.Equal("*.*", await page.Locator("#filesystem-right-wildcard").InputValueAsync());

            await page.Locator("#filesystem-left-wildcard").FillAsync("*.xml");
            await page.ClickAsync("#filesystem-left-apply-wildcard");
            await Visible("left", "a.xml");
            await Visible("left", "b.xml");
            await Visible("left", "nested");
            Assert.Equal(0, await Row("left", "c.json").CountAsync());

            var zipDownload = await page.RunAndWaitForDownloadAsync(
                () => page.ClickAsync("#filesystem-left-zip"));
            await using (var zipStream = File.OpenRead(await zipDownload.PathAsync()))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                Assert.Equal(
                    new[] { "a.xml", "b.xml" },
                    zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
            }

            await page.Locator("#filesystem-left-wildcard").FillAsync("*.*");
            await page.ClickAsync("#filesystem-left-apply-wildcard");
            await Visible("left", "c.json");

            await Row("left", "a.xml").ClickAsync();
            Assert.Equal(1, await SelectedCount("left"));
            await Row("left", "b.xml").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Control } });
            Assert.Equal(2, await SelectedCount("left"));
            await Row("left", "c.json").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Shift } });
            Assert.Equal(3, await SelectedCount("left"));
            Assert.Equal(0, await SelectedCount("right"));
            Assert.Equal(0, await Row("left", "nested").Locator(".selected-row").CountAsync());

            await page.ClickAsync("#filesystem-left-refresh");
            Assert.Equal(3, await SelectedCount("left"));
            await page.ClickAsync("#filesystem-left thead th[data-sort-key='size'] .sort-button");
            Assert.Equal(3, await SelectedCount("left"));

            await page.Locator("#filesystem-left-find-names").FillAsync("a.xml\nb.xml\nmissing.xml");
            await page.ClickAsync("#filesystem-left-find");
            Assert.True(await Row("left", "a.xml").EvaluateAsync<bool>("row => row.classList.contains('selected-row')"));
            Assert.True(await Row("left", "b.xml").EvaluateAsync<bool>("row => row.classList.contains('selected-row')"));
            Assert.Equal("archive / incoming", (await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync()).Trim());

            await Row("left", "c.json").ClickAsync();
            Assert.Equal(1, await SelectedCount("left"));
            var batchCountBefore = filesystemRequests.Count(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/move");
            await page.ClickAsync("#filesystem-left-move-selected");
            await Row("left", "c.json").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Visible("right", "c.json");
            var batchRequests = filesystemRequests.Where(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/move").ToArray();
            Assert.Equal(batchCountBefore + 1, batchRequests.Length);
            using (var payload = JsonDocument.Parse(batchRequests[^1].PostData!))
            {
                Assert.Equal("archive/incoming/", payload.RootElement.GetProperty("sourcePath").GetString());
                Assert.Equal("archive/processed/", payload.RootElement.GetProperty("destinationPath").GetString());
                Assert.Equal(
                    new[] { "c.json" },
                    payload.RootElement.GetProperty("fileNames").EnumerateArray().Select(value => value.GetString()).ToArray());
            }

            await Row("left", "a.xml").ClickAsync();
            await Row("left", "b.xml").ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Control } });
            var twoFileBatchBefore = batchRequests.Length;
            await page.ClickAsync("#filesystem-left-move-selected");
            await Visible("right", "a.xml");
            await Visible("right", "b.xml");
            var afterTwoFileBatch = filesystemRequests.Where(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/move").ToArray();
            Assert.Equal(twoFileBatchBefore + 1, afterTwoFileBatch.Length);
            using (var payload = JsonDocument.Parse(afterTwoFileBatch[^1].PostData!))
            {
                Assert.Equal(
                    new[] { "a.xml", "b.xml" },
                    payload.RootElement.GetProperty("fileNames").EnumerateArray().Select(value => value.GetString()).ToArray());
            }

            var directoryMoveBefore = filesystemRequests.Count(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/directory/move");
            await Row("left", "nested").Locator("[data-action=move]").ClickAsync();
            await Row("left", "nested").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Visible("right", "nested");
            Assert.Equal(
                directoryMoveBefore + 1,
                filesystemRequests.Count(request =>
                    request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/directory/move"));

            var upload = Path.Combine(temp, "upload.bin");
            var uploadBytes = "browser-opaque-payload"u8.ToArray();
            await File.WriteAllBytesAsync(upload, uploadBytes);
            var uploadBefore = filesystemRequests.Count(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/file");
            var chooser = await page.RunAndWaitForFileChooserAsync(
                () => page.ClickAsync("#filesystem-left-upload"));
            await chooser.SetFilesAsync(upload);
            await Visible("left", "upload.bin");
            Assert.Equal(
                uploadBefore + 1,
                filesystemRequests.Count(request =>
                    request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/file"));
            Assert.DoesNotContain(filesystemRequests, request => request.Method is "PUT" or "DELETE");

            var download = await page.RunAndWaitForDownloadAsync(
                () => Row("left", "upload.bin").Locator("[data-entry-open]").ClickAsync());
            Assert.Equal(
                SHA256.HashData(uploadBytes),
                SHA256.HashData(await File.ReadAllBytesAsync(await download.PathAsync())));

            var renameBefore = filesystemRequests.Count(request =>
                request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/rename");
            prompts.Enqueue("renamed.bin");
            await Row("left", "upload.bin").Locator("[data-action=rename]").ClickAsync();
            await Visible("left", "renamed.bin");
            Assert.Equal(
                renameBefore + 1,
                filesystemRequests.Count(request =>
                    request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/v1/filesystem/rename"));

            await page.ClickAsync("#filesystem-right-breadcrumb [data-path='archive/']");
            await WaitBreadcrumb("right", "archive");
            await Row("right", "incoming").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("right", "archive / incoming");
            await Visible("right", "renamed.bin");
            await Row("left", "renamed.bin").ClickAsync();
            Assert.True(await page.Locator("#filesystem-left-move-selected").IsDisabledAsync());
            Assert.True(await page.Locator("#filesystem-right-move-selected").IsDisabledAsync());

            await page.Locator("#filesystem-left-find-names").FillAsync("definitely-missing.bin");
            await page.ClickAsync("#filesystem-left-find");
            Assert.Contains(
                "0",
                await page.Locator("#filesystem-left-status").InnerTextAsync(),
                StringComparison.Ordinal);
            Assert.Contains(
                "archive / incoming",
                await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync());

            await Prompt("#filesystem-left-create-directory", "local-dir");
            await Visible("left", "local-dir");
            await Row("left", "local-dir").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("left", "archive / incoming / local-dir");
            Assert.Equal(0, await SelectedCount("left"));
            await page.Locator("#filesystem-left-entries tr[data-parent-row='true']").WaitForAsync();
            Assert.Equal(
                0,
                await page.Locator("#filesystem-left-entries tr[data-parent-row='true'] [data-action]").CountAsync());
            await page.Locator("#filesystem-left-entries tr[data-parent-row='true'] [data-entry-open]").ClickAsync();
            await WaitBreadcrumb("left", "archive / incoming");

            var external = Path.Combine(incoming, "external.txt");
            await File.WriteAllTextAsync(external, "external");
            await page.ClickAsync("#filesystem-left-refresh");
            await Visible("left", "external.txt");
            await Row("left", "external.txt").ClickAsync();
            Assert.Equal(1, await SelectedCount("left"));
            File.Move(external, Path.Combine(incoming, "external-renamed.txt"));
            await page.ClickAsync("#filesystem-left-refresh");
            await Visible("left", "external-renamed.txt");
            Assert.Equal(0, await SelectedCount("left"));

            await page.ClickAsync("#filesystem-roots [data-root='nfs']");
            await WaitBreadcrumb("left", "nfs");
            await WaitBreadcrumb("right", "nfs");
            Assert.Equal(0, await SelectedCount("left"));
            Assert.Equal(0, await SelectedCount("right"));

            var nfsExternal = Path.Combine(nfs, "nfs-external.txt");
            await File.WriteAllTextAsync(nfsExternal, "nfs");
            await page.ClickAsync("#filesystem-right-refresh");
            await Visible("right", "nfs-external.txt");

            await page.ClickAsync("#filesystem-roots [data-root='offline']");
            await page.Locator("#filesystem-left-status")
                .GetByText("filesystem_root_unavailable", new() { Exact = false })
                .WaitForAsync();
            await page.Locator("#filesystem-right-status")
                .GetByText("filesystem_root_unavailable", new() { Exact = false })
                .WaitForAsync();
            Assert.False(Directory.Exists(offline));
            Assert.Empty(pageErrors);
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

        throw new TimeoutException("WebAssistant browser test server did not start.");
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
