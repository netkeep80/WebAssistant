using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemBrowserTests
{
    [Fact]
    public async Task Browser_ExercisesMultiRootTwoPanelWorkflow()
    {
        var repo = FindRoot();
        var temp = Path.Combine(
            Path.GetTempPath(),
            "webassistant-browser",
            Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(temp, "archive");
        var nfs = Path.Combine(temp, "nfs");
        var logs = Path.Combine(temp, "logs");
        Directory.CreateDirectory(Path.Combine(archive, "incoming"));
        Directory.CreateDirectory(Path.Combine(archive, "processed"));
        Directory.CreateDirectory(nfs);
        Directory.CreateDirectory(logs);
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
                .Locator("tr")
                .Filter(new() { HasTextString = name });
            async Task Visible(string side, string name) =>
                await Row(side, name).WaitForAsync();
            async Task Prompt(string selector, string value)
            {
                prompts.Enqueue(value);
                await page.ClickAsync(selector);
            }

            await page.Locator("#filesystem-roots [data-root='archive']").WaitForAsync();
            await page.Locator("#filesystem-roots [data-root='nfs']").WaitForAsync();
            await page.ClickAsync("#filesystem-roots [data-root='archive']");

            Assert.Equal(
                "archive",
                (await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync()).Trim());
            Assert.Equal(
                "archive",
                (await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync()).Trim());

            await Row("left", "incoming").Locator("[data-entry-open]").ClickAsync();
            await Row("right", "processed").Locator("[data-entry-open]").ClickAsync();
            Assert.Contains(
                "archive / incoming",
                await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync());
            Assert.Contains(
                "archive / processed",
                await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync());

            var upload = Path.Combine(temp, "a.bin");
            var bytes = "browser-opaque-payload"u8.ToArray();
            await File.WriteAllBytesAsync(upload, bytes);
            var uploadResponseSource = new TaskCompletionSource<IResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<IResponse> observeUpload = (_, response) =>
            {
                if (response.Request.Method == "PUT" &&
                    response.Url.Contains("/v1/filesystem/file", StringComparison.Ordinal))
                {
                    uploadResponseSource.TrySetResult(response);
                }
            };
            page.Response += observeUpload;
            var chooser = await page.RunAndWaitForFileChooserAsync(
                () => page.ClickAsync("#filesystem-left-upload"));
            await chooser.SetFilesAsync(upload);
            var uploadResponse = await uploadResponseSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
            page.Response -= observeUpload;
            Assert.True(
                uploadResponse.Ok,
                $"Browser upload PUT returned HTTP {uploadResponse.Status}: {await uploadResponse.TextAsync()}");
            await Visible("left", "a.bin");
            Assert.Equal(0, await Row("right", "a.bin").CountAsync());

            var download = await page.RunAndWaitForDownloadAsync(
                () => Row("left", "a.bin").Locator("[data-entry-open]").ClickAsync());
            Assert.Equal(
                SHA256.HashData(bytes),
                SHA256.HashData(await File.ReadAllBytesAsync(await download.PathAsync())));

            await Row("left", "a.bin").Locator("[data-action=move]").ClickAsync();
            await Row("left", "a.bin").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Visible("right", "a.bin");

            await Row("right", "a.bin").Locator("[data-action=move]").ClickAsync();
            await Visible("left", "a.bin");
            await Row("right", "a.bin").WaitForAsync(new() { State = WaitForSelectorState.Detached });

            await page.ClickAsync("#filesystem-right-breadcrumb [data-path='archive/']");
            await Row("right", "incoming").Locator("[data-entry-open]").ClickAsync();
            await Visible("right", "a.bin");
            Assert.True(await Row("left", "a.bin").Locator("[data-action=move]").IsDisabledAsync());
            Assert.True(await Row("right", "a.bin").Locator("[data-action=move]").IsDisabledAsync());

            prompts.Enqueue("b.bin");
            await Row("left", "a.bin").Locator("[data-action=rename]").ClickAsync();
            await Visible("left", "b.bin");
            await Visible("right", "b.bin");

            await Prompt("#filesystem-left-create-directory", "nested");
            await Visible("left", "nested");
            await Row("left", "nested").Locator("[data-entry-open]").ClickAsync();
            await page.Locator("#filesystem-left-entries tr[data-parent-row='true']").WaitForAsync();
            Assert.Equal(0, await page.Locator(
                "#filesystem-left-entries tr[data-parent-row='true'] [data-action]").CountAsync());
            await page.Locator("#filesystem-left-entries tr[data-parent-row='true'] [data-entry-open]").ClickAsync();
            Assert.Contains(
                "archive / incoming",
                await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync());

            await Prompt("#filesystem-left-create-file", "empty.txt");
            await Visible("left", "empty.txt");
            Assert.Equal(0, new FileInfo(Path.Combine(archive, "incoming", "empty.txt")).Length);

            await page.SelectOptionAsync("#filesystem-left-sort-key", "size");
            await page.ClickAsync("#filesystem-left-sort-direction");
            Assert.Equal(
                "name",
                await page.Locator("#filesystem-right-sort-key").InputValueAsync());

            await page.ClickAsync("#filesystem-left-breadcrumb [data-path='archive/']");
            await Visible("left", "blocked.sh");
            Assert.Contains(
                "entry-restricted",
                await Row("left", "blocked.sh").GetAttributeAsync("class") ?? string.Empty);
            Assert.Equal(
                "true",
                await Row("left", "blocked.sh").Locator("[data-entry-open]").GetAttributeAsync("aria-disabled"));

            var external = Path.Combine(archive, "external.txt");
            await File.WriteAllTextAsync(external, "external");
            await page.ClickAsync("#filesystem-left-refresh");
            await Visible("left", "external.txt");
            File.Move(external, Path.Combine(archive, "renamed.txt"));
            await page.ClickAsync("#filesystem-left-refresh");
            await Visible("left", "renamed.txt");

            await page.ClickAsync("#filesystem-roots [data-root='nfs']");
            Assert.Equal(
                "nfs",
                (await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync()).Trim());
            Assert.Equal(
                "nfs",
                (await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync()).Trim());
            Assert.Equal(0, await Row("left", "incoming").CountAsync());
            Assert.Equal(0, await Row("right", "incoming").CountAsync());

            var nfsExternal = Path.Combine(nfs, "nfs-external.txt");
            await File.WriteAllTextAsync(nfsExternal, "nfs");
            await page.ClickAsync("#filesystem-right-refresh");
            await Visible("right", "nfs-external.txt");
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
