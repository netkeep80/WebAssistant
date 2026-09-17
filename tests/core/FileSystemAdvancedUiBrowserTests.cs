using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemAdvancedUiBrowserTests
{
    [Fact]
    public async Task Browser_ExercisesViewportSplitterKeyboardAndDragDrop()
    {
        var repo = FindRoot();
        var temp = Path.Combine(
            Path.GetTempPath(),
            "webassistant-browser-227",
            Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(temp, "archive");
        var leftDirectory = Path.Combine(archive, "left");
        var rightDirectory = Path.Combine(archive, "right");
        var logs = Path.Combine(temp, "logs");
        Directory.CreateDirectory(leftDirectory);
        Directory.CreateDirectory(rightDirectory);
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(leftDirectory, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(leftDirectory, "b.txt"), "bb");
        await File.WriteAllTextAsync(Path.Combine(rightDirectory, "r.txt"), "rrr");
        for (var i = 0; i < 80; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(archive, $"filler-{i:D3}.txt"),
                new string('x', i + 1));
        }

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
            await page.SetViewportSizeAsync(1400, 700);
            var pageErrors = new List<string>();
            page.PageError += (_, error) => pageErrors.Add(error);

            await page.GotoAsync(baseUrl + "/filesystem.html");
            await page.Locator("#filesystem-roots [data-root='archive']").WaitForAsync();
            await page.ClickAsync("#filesystem-roots [data-root='archive']");
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#filesystem-left-entries tr').length >= 80");

            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollHeight <= document.documentElement.clientHeight && document.documentElement.scrollWidth <= document.documentElement.clientWidth"),
                "Страница filesystem.html не должна иметь общего scroll документа.");
            foreach (var side in new[] { "left", "right" })
            {
                Assert.True(
                    await page.EvaluateAsync<bool>(
                        "side => { const e=document.getElementById(`filesystem-${side}-table-wrap`); return e.scrollWidth <= e.clientWidth + 1; }",
                        side),
                    $"Панель {side} не должна иметь горизонтальную прокрутку.");
            }

            await page.EvaluateAsync(
                "() => { document.getElementById('filesystem-left-table-wrap').scrollTop=220; document.getElementById('filesystem-right-table-wrap').scrollTop=0; }");
            Assert.True(await page.EvaluateAsync<double>(
                "() => document.getElementById('filesystem-left-table-wrap').scrollTop") > 0);
            Assert.Equal(0, await page.EvaluateAsync<double>(
                "() => document.getElementById('filesystem-right-table-wrap').scrollTop"));

            var leftBefore = await page.Locator("#filesystem-left").BoundingBoxAsync();
            var rightBefore = await page.Locator("#filesystem-right").BoundingBoxAsync();
            var splitter = await page.Locator("#filesystem-splitter").BoundingBoxAsync();
            Assert.NotNull(leftBefore);
            Assert.NotNull(rightBefore);
            Assert.NotNull(splitter);
            await page.Mouse.MoveAsync(splitter!.X + splitter.Width / 2, splitter.Y + 30);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(splitter.X + splitter.Width / 2 + 120, splitter.Y + 30);
            await page.Mouse.UpAsync();
            var leftAfter = await page.Locator("#filesystem-left").BoundingBoxAsync();
            var rightAfter = await page.Locator("#filesystem-right").BoundingBoxAsync();
            Assert.NotNull(leftAfter);
            Assert.NotNull(rightAfter);
            Assert.True(leftAfter!.Width > leftBefore!.Width + 60);
            Assert.True(rightAfter!.Width < rightBefore!.Width - 60);
            Assert.True(await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));

            await page.ClickAsync("#filesystem-left thead th[data-sort-key='size'] .sort-button");
            Assert.Equal(
                "ascending",
                await page.Locator("#filesystem-left thead th[data-sort-key='size']")
                    .GetAttributeAsync("aria-sort"));
            Assert.Equal(
                "ascending",
                await page.Locator("#filesystem-right thead th[data-sort-key='name']")
                    .GetAttributeAsync("aria-sort"));
            await page.ClickAsync("#filesystem-left thead th[data-sort-key='size'] .sort-button");
            Assert.Equal(
                "descending",
                await page.Locator("#filesystem-left thead th[data-sort-key='size']")
                    .GetAttributeAsync("aria-sort"));

            ILocator Row(string side, string name) => page
                .Locator($"#filesystem-{side}-entries tr")
                .Filter(new() { HasTextString = name });
            async Task WaitBreadcrumb(string side, string expected) =>
                await page.WaitForFunctionAsync(
                    "args => document.getElementById(args.id).innerText.includes(args.expected)",
                    new { id = $"filesystem-{side}-breadcrumb", expected });

            await Row("left", "left").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("left", "archive / left");
            await Row("right", "right").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("right", "archive / right");

            var parent = page.Locator("#filesystem-left-entries tr[data-parent-row='true']");
            await parent.Locator("td").Nth(1).ClickAsync();
            Assert.Contains("current-row", await parent.GetAttributeAsync("class") ?? string.Empty);
            var parentSelection = await parent.GetAttributeAsync("data-selection-id");
            await page.Keyboard.PressAsync("ArrowDown");
            var selectedAfterDown = page.Locator("#filesystem-left-entries tr.current-row");
            Assert.Equal(1, await selectedAfterDown.CountAsync());
            Assert.NotEqual(parentSelection, await selectedAfterDown.GetAttributeAsync("data-selection-id"));

            var leftPathBeforeTab = await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync();
            var rightPathBeforeTab = await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.Contains(
                "current-panel",
                await page.Locator("#filesystem-right").GetAttributeAsync("class") ?? string.Empty);
            Assert.Equal(leftPathBeforeTab, await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync());
            Assert.Equal(rightPathBeforeTab, await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync());

            await page.Keyboard.PressAsync("ArrowDown");
            Assert.Equal(1, await page.Locator("#filesystem-right-entries tr.current-row").CountAsync());
            await page.Keyboard.PressAsync("Enter");
            await WaitBreadcrumb("right", "archive");
            Assert.Equal(
                "archive",
                (await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync()).Trim());

            await page.EvaluateAsync(
                """
                () => {
                  const panel = document.getElementById('filesystem-left');
                  const dt = new DataTransfer();
                  dt.items.add(new File(['dropped'], 'dropped.txt', { type: 'application/octet-stream' }));
                  for (const type of ['dragenter', 'dragover', 'drop']) {
                    panel.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt }));
                  }
                }
                """);
            await Row("left", "dropped.txt").WaitForAsync();
            Assert.True(File.Exists(Path.Combine(leftDirectory, "dropped.txt")));

            await Row("left", "dropped.txt").DragToAsync(page.Locator("#filesystem-right"));
            await Row("left", "dropped.txt").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Row("right", "dropped.txt").WaitForAsync();
            Assert.False(File.Exists(Path.Combine(leftDirectory, "dropped.txt")));
            Assert.True(File.Exists(Path.Combine(archive, "dropped.txt")));

            await page.ClickAsync("#filesystem-left-breadcrumb [data-path='archive/']");
            await WaitBreadcrumb("left", "archive");
            await Row("right", "dropped.txt").DragToAsync(page.Locator("#filesystem-left"));
            Assert.True(File.Exists(Path.Combine(archive, "dropped.txt")));
            Assert.Equal(1, await Row("left", "dropped.txt").CountAsync());
            Assert.Equal(1, await Row("right", "dropped.txt").CountAsync());

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
