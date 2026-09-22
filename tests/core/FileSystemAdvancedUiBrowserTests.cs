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
        var nfs = Path.Combine(temp, "nfs");
        var leftDirectory = Path.Combine(archive, "left");
        var rightDirectory = Path.Combine(archive, "right");
        var movingDirectory = Path.Combine(leftDirectory, "move-dir");
        var logs = Path.Combine(temp, "logs");
        Directory.CreateDirectory(leftDirectory);
        Directory.CreateDirectory(rightDirectory);
        Directory.CreateDirectory(movingDirectory);
        Directory.CreateDirectory(nfs);
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(leftDirectory, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(leftDirectory, "b.txt"), "bb");
        await File.WriteAllTextAsync(Path.Combine(leftDirectory, "conflict.txt"), "left-conflict");
        await File.WriteAllTextAsync(Path.Combine(movingDirectory, "nested.txt"), "nested");
        await File.WriteAllTextAsync(Path.Combine(rightDirectory, "r.txt"), "rrr");
        await File.WriteAllTextAsync(Path.Combine(rightDirectory, "conflict.txt"), "right-conflict");
        await File.WriteAllTextAsync(Path.Combine(archive, "blocked.sh"), "echo blocked");
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
            await page.SetViewportSizeAsync(1400, 700);
            var pageErrors = new List<string>();
            page.PageError += (_, error) => pageErrors.Add(error);

            await page.GotoAsync(baseUrl + "/filesystem.html");
            await page.Locator("#filesystem-roots [data-root='archive']").WaitForAsync();
            await page.Locator("#filesystem-roots [data-root='nfs']").WaitForAsync();
            await page.ClickAsync("#filesystem-roots [data-root='archive']");
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('#filesystem-left-entries tr').length >= 80");

            ILocator Row(string side, string name) => page
                .Locator($"#filesystem-{side}-entries tr[data-selection-id$='/{name}']");
            async Task WaitBreadcrumb(string side, string expected) =>
                await page.WaitForFunctionAsync(
                    "args => document.getElementById(args.id).innerText.trim() === args.expected",
                    new { id = $"filesystem-{side}-breadcrumb", expected });
            async Task AssertNoHorizontalScroll()
            {
                Assert.True(
                    await page.EvaluateAsync<bool>(
                        "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"),
                    "Страница filesystem.html не должна иметь горизонтальную прокрутку.");
                foreach (var side in new[] { "left", "right" })
                {
                    Assert.True(
                        await page.EvaluateAsync<bool>(
                            "side => { const e=document.getElementById(`filesystem-${side}-table-wrap`); return e.scrollWidth <= e.clientWidth + 1; }",
                            side),
                        $"Панель {side} не должна иметь горизонтальную прокрутку.");
                }
            }
            async Task ExternalDrop(string side, string name, string contents)
            {
                await page.EvaluateAsync(
                    """
                    args => {
                      const panel = document.getElementById(`filesystem-${args.side}`);
                      const dt = new DataTransfer();
                      dt.items.add(new File([args.contents], args.name, { type: 'application/octet-stream' }));
                      for (const type of ['dragenter', 'dragover', 'drop']) {
                        panel.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt }));
                      }
                    }
                    """,
                    new { side, name, contents });
            }
            async Task DragSplitterTo(float x)
            {
                var box = await page.Locator("#filesystem-splitter").BoundingBoxAsync();
                Assert.NotNull(box);
                await page.Mouse.MoveAsync(box!.X + box.Width / 2, box.Y + 30);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(x, box.Y + 30);
                await page.Mouse.UpAsync();
            }

            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollHeight <= document.documentElement.clientHeight + 1"),
                "Страница filesystem.html не должна иметь общего вертикального scroll документа.");
            await AssertNoHorizontalScroll();
            var bodyFontSize = await page.EvaluateAsync<double>(
                "() => parseFloat(getComputedStyle(document.body).fontSize)");
            var tableFontSize = await page.EvaluateAsync<double>(
                "() => parseFloat(getComputedStyle(document.querySelector('#filesystem-left-table-wrap')).fontSize)");
            Assert.True(
                tableFontSize < bodyFontSize,
                "Табличная часть должна использовать более компактный шрифт, чем общий интерфейс страницы.");

            var initialLeft = await page.Locator("#filesystem-left").BoundingBoxAsync();
            var initialRight = await page.Locator("#filesystem-right").BoundingBoxAsync();
            Assert.NotNull(initialLeft);
            Assert.NotNull(initialRight);
            Assert.True(
                Math.Abs(initialLeft!.Width - initialRight!.Width) < 30,
                "Начальная геометрия должна быть примерно 50/50.");

            await page.EvaluateAsync(
                "() => { document.getElementById('filesystem-left-table-wrap').scrollTop=220; document.getElementById('filesystem-right-table-wrap').scrollTop=0; }");
            var leftScrollBeforeResize = await page.EvaluateAsync<double>(
                "() => document.getElementById('filesystem-left-table-wrap').scrollTop");
            Assert.True(leftScrollBeforeResize > 0);
            Assert.Equal(0, await page.EvaluateAsync<double>(
                "() => document.getElementById('filesystem-right-table-wrap').scrollTop"));

            await page.ClickAsync("#filesystem-left thead th[data-sort-key='size'] .sort-button");
            await page.ClickAsync("#filesystem-left thead th[data-sort-key='size'] .sort-button");
            Assert.Equal(
                "descending",
                await page.Locator("#filesystem-left thead th[data-sort-key='size']")
                    .GetAttributeAsync("aria-sort"));
            Assert.Equal(
                "ascending",
                await page.Locator("#filesystem-right thead th[data-sort-key='name']")
                    .GetAttributeAsync("aria-sort"));

            var selectedRootRow = Row("left", "filler-010.txt");
            await selectedRootRow.Locator("td").Nth(1).ClickAsync();
            var rootSelection = await selectedRootRow.GetAttributeAsync("data-selection-id");
            Assert.NotNull(rootSelection);

            var splitterBefore = await page.Locator("#filesystem-splitter").BoundingBoxAsync();
            Assert.NotNull(splitterBefore);
            await DragSplitterTo(splitterBefore!.X + 120);
            var widerLeft = await page.Locator("#filesystem-left").BoundingBoxAsync();
            var narrowerRight = await page.Locator("#filesystem-right").BoundingBoxAsync();
            Assert.NotNull(widerLeft);
            Assert.NotNull(narrowerRight);
            Assert.True(widerLeft!.Width > initialLeft.Width + 60);
            Assert.True(narrowerRight!.Width < initialRight.Width - 60);
            Assert.Equal(
                rootSelection,
                await page.Locator("#filesystem-left-entries tr.current-row").GetAttributeAsync("data-selection-id"));
            Assert.Equal(
                "descending",
                await page.Locator("#filesystem-left thead th[data-sort-key='size']")
                    .GetAttributeAsync("aria-sort"));
            Assert.True(
                await page.EvaluateAsync<double>(
                    "() => document.getElementById('filesystem-left-table-wrap').scrollTop") > 0);
            Assert.Equal(0, await page.EvaluateAsync<double>(
                "() => document.getElementById('filesystem-right-table-wrap').scrollTop"));
            await AssertNoHorizontalScroll();

            await DragSplitterTo(10000);
            var minimumRight = await page.Locator("#filesystem-right").BoundingBoxAsync();
            Assert.NotNull(minimumRight);
            Assert.True(minimumRight!.Width >= 350, "Правая панель не должна сжиматься ниже рабочего минимума.");
            await AssertNoHorizontalScroll();

            await DragSplitterTo(-1000);
            var minimumLeft = await page.Locator("#filesystem-left").BoundingBoxAsync();
            Assert.NotNull(minimumLeft);
            Assert.True(minimumLeft!.Width >= 350, "Левая панель не должна сжиматься ниже рабочего минимума.");
            await AssertNoHorizontalScroll();

            var workspace = await page.Locator("#filesystem-panels").BoundingBoxAsync();
            Assert.NotNull(workspace);
            await DragSplitterTo(workspace!.X + workspace.Width / 2);
            await AssertNoHorizontalScroll();

            await Row("left", "left").Locator("td").Nth(1).ClickAsync();
            await page.Keyboard.PressAsync("Enter");
            await WaitBreadcrumb("left", "archive / left");
            await Row("right", "right").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("right", "archive / right");

            var aRow = Row("left", "a.txt");
            await aRow.Locator("td").Nth(1).ClickAsync();
            var aSelection = await aRow.GetAttributeAsync("data-selection-id");
            Assert.NotNull(aSelection);
            var keyboardDownload = await page.RunAndWaitForDownloadAsync(
                () => page.Keyboard.PressAsync("Enter"));
            Assert.Equal("a.txt", keyboardDownload.SuggestedFilename);

            await page.ClickAsync("#filesystem-left thead th[data-sort-key='name'] .sort-button");
            Assert.Equal(
                aSelection,
                await page.Locator("#filesystem-left-entries tr.current-row").GetAttributeAsync("data-selection-id"));
            await page.ClickAsync("#filesystem-left-refresh");
            Assert.Equal(
                aSelection,
                await page.Locator("#filesystem-left-entries tr.current-row").GetAttributeAsync("data-selection-id"));

            var leftParent = page.Locator("#filesystem-left-entries tr[data-parent-row='true']");
            await leftParent.Locator("td").Nth(1).ClickAsync();
            var leftParentSelection = await leftParent.GetAttributeAsync("data-selection-id");
            await page.Keyboard.PressAsync("ArrowDown");
            var leftSelectedAfterDown = await page.Locator("#filesystem-left-entries tr.current-row")
                .GetAttributeAsync("data-selection-id");
            Assert.NotEqual(leftParentSelection, leftSelectedAfterDown);
            var savedLeftSelection = leftSelectedAfterDown;

            var rRow = Row("right", "r.txt");
            await rRow.Locator("td").Nth(1).ClickAsync();
            var savedRightSelection = await rRow.GetAttributeAsync("data-selection-id");
            Assert.NotNull(savedRightSelection);
            var leftPathBeforeTab = await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync();
            var rightPathBeforeTab = await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.Contains(
                "current-panel",
                await page.Locator("#filesystem-left").GetAttributeAsync("class") ?? string.Empty);
            Assert.Equal(
                savedLeftSelection,
                await page.Locator("#filesystem-left-entries tr.current-row").GetAttributeAsync("data-selection-id"));
            await page.Keyboard.PressAsync("Tab");
            Assert.Contains(
                "current-panel",
                await page.Locator("#filesystem-right").GetAttributeAsync("class") ?? string.Empty);
            Assert.Equal(
                savedRightSelection,
                await page.Locator("#filesystem-right-entries tr.current-row").GetAttributeAsync("data-selection-id"));
            Assert.Equal(leftPathBeforeTab, await page.Locator("#filesystem-left-breadcrumb").InnerTextAsync());
            Assert.Equal(rightPathBeforeTab, await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync());

            var rightParent = page.Locator("#filesystem-right-entries tr[data-parent-row='true']");
            await rightParent.Locator("td").Nth(1).ClickAsync();
            await page.Keyboard.PressAsync("Enter");
            await WaitBreadcrumb("right", "archive");
            Assert.Equal(
                "archive",
                (await page.Locator("#filesystem-right-breadcrumb").InnerTextAsync()).Trim());

            await ExternalDrop("left", "left-drop.txt", "left-drop");
            await Row("left", "left-drop.txt").WaitForAsync();
            Assert.True(File.Exists(Path.Combine(leftDirectory, "left-drop.txt")));
            await ExternalDrop("right", "right-drop.txt", "right-drop");
            await Row("right", "right-drop.txt").WaitForAsync();
            Assert.True(File.Exists(Path.Combine(archive, "right-drop.txt")));

            await Row("left", "left-drop.txt").DragToAsync(page.Locator("#filesystem-right"));
            await Row("left", "left-drop.txt").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Row("right", "left-drop.txt").WaitForAsync();
            Assert.False(File.Exists(Path.Combine(leftDirectory, "left-drop.txt")));
            Assert.True(File.Exists(Path.Combine(archive, "left-drop.txt")));

            await Row("right", "left-drop.txt").DragToAsync(page.Locator("#filesystem-left"));
            await Row("right", "left-drop.txt").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Row("left", "left-drop.txt").WaitForAsync();
            Assert.True(File.Exists(Path.Combine(leftDirectory, "left-drop.txt")));
            Assert.False(File.Exists(Path.Combine(archive, "left-drop.txt")));

            await Row("left", "move-dir").DragToAsync(page.Locator("#filesystem-right"));
            await Row("left", "move-dir").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Row("right", "move-dir").WaitForAsync();
            Assert.False(Directory.Exists(movingDirectory));
            Assert.True(File.Exists(Path.Combine(archive, "move-dir", "nested.txt")));

            await page.ClickAsync("#filesystem-right-breadcrumb [data-path='archive/']");
            await Row("right", "right").Locator("[data-entry-open]").ClickAsync();
            await WaitBreadcrumb("right", "archive / right");
            var moveResponse = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void CaptureMoveResponse(object? _, IResponse response)
            {
                if (response.Request.Method == "POST" &&
                    new Uri(response.Url).AbsolutePath == "/v1/filesystem/move")
                {
                    moveResponse.TrySetResult(response.Status);
                }
            }
            page.Response += CaptureMoveResponse;
            try
            {
                await Row("left", "conflict.txt").DragToAsync(page.Locator("#filesystem-right"));
                Assert.Equal(
                    (int)HttpStatusCode.OK,
                    await moveResponse.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                page.Response -= CaptureMoveResponse;
            }
            Assert.Equal("left-conflict", await File.ReadAllTextAsync(Path.Combine(leftDirectory, "conflict.txt")));
            Assert.Equal("right-conflict", await File.ReadAllTextAsync(Path.Combine(rightDirectory, "conflict.txt")));

            await page.ClickAsync("#filesystem-left-breadcrumb [data-path='archive/']");
            await page.ClickAsync("#filesystem-right-breadcrumb [data-path='archive/']");
            await WaitBreadcrumb("left", "archive");
            await WaitBreadcrumb("right", "archive");
            Assert.NotEqual("true", await Row("left", "blocked.sh").GetAttributeAsync("draggable"));
            await Row("left", "right-drop.txt").DragToAsync(page.Locator("#filesystem-right"));
            Assert.True(File.Exists(Path.Combine(archive, "right-drop.txt")));
            Assert.Equal(1, await Row("left", "right-drop.txt").CountAsync());
            Assert.Equal(1, await Row("right", "right-drop.txt").CountAsync());

            await Row("left", "filler-000.txt").Locator("td").Nth(1).ClickAsync();
            Assert.Equal(1, await page.Locator("#filesystem-left-entries tr.current-row").CountAsync());
            await page.ClickAsync("#filesystem-roots [data-root='nfs']");
            await WaitBreadcrumb("left", "nfs");
            await WaitBreadcrumb("right", "nfs");
            Assert.Equal(0, await page.Locator("#filesystem-left-entries tr.current-row").CountAsync());
            Assert.Equal(0, await page.Locator("#filesystem-right-entries tr.current-row").CountAsync());
            Assert.Equal(0, await page.Locator(".panel.drop-target,.panel.drop-forbidden").CountAsync());

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