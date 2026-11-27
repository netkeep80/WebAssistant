using System.Text;
using NAPS2.Images.Gtk;
using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class LinuxVirtualScanAdapterTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "LinuxVirtualScanner")]
    public async Task VirtualSaneScanner_ProducesPdfThroughDirectSdkProductAdapter()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_LINUX_VIRTUAL") != "1")
        {
            return;
        }

        var tempDirectory = Path.GetTempPath();
        var before = Directory
            .GetFiles(tempDirectory, "webassistant-*.pdf")
            .ToHashSet(StringComparer.Ordinal);

        using var adapter = new LinuxScanAdapter();
        var discovery = await adapter.GetScannersAsync();
        var virtualDevices = discovery.Scanners
            .Where(device => device.Name.Contains(
                "frontend-tester",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(virtualDevices);
        Assert.All(
            virtualDevices,
            device => Assert.StartsWith("wa2-sane-", device.Id, StringComparison.Ordinal));
        Assert.Equal(
            virtualDevices.Length,
            virtualDevices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count());

        var scanner = Assert.Single(
            virtualDevices,
            device => string.Equals(
                device.Id,
                ScannerIdentity.Create(ScannerBackend.Sane, "test:0"),
                StringComparison.Ordinal));

        await using var pdf = await adapter.ScanAsync(scanner.Id, ScanSource.Glass);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        Assert.True(bytes.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

        var leaked = Directory
            .GetFiles(tempDirectory, "webassistant-*.pdf")
            .Where(path => !before.Contains(path))
            .ToArray();
        Assert.Empty(leaked);
    }

    [Fact]
    [Trait("Category", "LinuxVirtualScanner")]
    public async Task Research_SaneRegistrationRefresh_SameProcessAndExplicitExit()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_LINUX_VIRTUAL") != "1")
            return;

        // Меняем только временную конфигурацию, подготовленную штатным CI.
        var directory = Environment.GetEnvironmentVariable("SANE_CONFIG_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        var path = Path.Combine(directory!, "dll.conf");
        var original = File.ReadAllText(path);
        Assert.Equal("test", original.Trim());
        const string nativeId = "test:0";
        static ScanOptions EndSession() => new()
        {
            Driver = Driver.Sane,
            SaneOptions = new SaneOptions { KeepInitialized = false }
        };
        static bool ContainsTest(IEnumerable<ScanDevice> devices) =>
            devices.Any(device => device.ID == "test:0");

        using var context = new ScanningContext(new GtkImageContext());
        var controller = new ScanController(context);
        try
        {
            // Завершаем ранее инициализированную сессию, если она была.
            await controller.GetDeviceList(EndSession());
            var baseline = Assert.Single((await controller.GetDeviceList(Driver.Sane)).Where(device => device.ID == nativeId));
            var baselineId = ScannerIdentity.Create(ScannerBackend.Sane, baseline.ID);
            await controller.GetDeviceList(EndSession());
            File.WriteAllText(path, "# empty\n");
            Assert.False(ContainsTest(await controller.GetDeviceList(Driver.Sane)));

            File.WriteAllText(path, original);
            var live = ContainsTest(await controller.GetDeviceList(Driver.Sane));
            using var secondContext = new ScanningContext(new GtkImageContext());
            var secondController = new ScanController(secondContext);
            var newContext = ContainsTest(await secondController.GetDeviceList(Driver.Sane));
            var ending = ContainsTest(await controller.GetDeviceList(EndSession()));
            var refreshed = await controller.GetDeviceList(Driver.Sane);
            var refreshedPresent = ContainsTest(refreshed);

            // Исследуем предсказание из pinned SaneClient, а не подменяем результат.
            // Отказ содержит все четыре наблюдения для локализации.
            output.WriteLine($"B_SDK_MATRIX live={live} new_context={newContext} ending={ending} next={refreshedPresent}");
            Assert.True(!live && !newContext && !ending && refreshedPresent,
                $"B_SDK_MATRIX live={live} new_context={newContext} ending={ending} next={refreshedPresent}");
            var selected = Assert.Single(refreshed.Where(device => device.ID == nativeId));
            Assert.Equal(baselineId, ScannerIdentity.Create(ScannerBackend.Sane, selected.ID));
            output.WriteLine($"B_SDK_ID before={baselineId} after={ScannerIdentity.Create(ScannerBackend.Sane, selected.ID)}");
            var caps = await controller.GetCaps(selected);
            Assert.True(caps.PaperSourceCaps?.SupportsFlatbed == true);

            // Проверяем продуктовый adapter после явного завершения нативной сессии.
            using var adapter = new LinuxScanAdapter();
            var publicId = ScannerIdentity.Create(ScannerBackend.Sane, nativeId);
            var discovery = await adapter.GetScannersAsync();
            var scanner = Assert.Single(discovery.Scanners.Where(device => device.Id == publicId));
            var settings = await adapter.GetScannerCapabilitiesAsync(scanner.Id);
            Assert.NotNull(settings);
            Assert.Equal(publicId, settings!.Id);
            await using var pdf = await adapter.ScanAsync(publicId, ScanSource.Glass);
            var signature = new byte[5];
            Assert.Equal(5, await pdf.ReadAsync(signature.AsMemory()));
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(signature));
        }
        finally
        {
            File.WriteAllText(path, original);
            await controller.GetDeviceList(EndSession());
        }
    }


    [Fact]
    [Trait("Category", "LinuxVirtualScanner")]
    public async Task Research_SerializedRefresh_BoundariesCancellationAndRetry()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_LINUX_VIRTUAL") != "1")
            return;

        // Это экспериментальная граница, не изменение LinuxScanAdapter.
        using var context = new ScanningContext(new GtkImageContext());
        var controller = new ScanController(context);
        using var adapter = new LinuxScanAdapter();
        using var gate = new SemaphoreSlim(1, 1);
        using var waitCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string nativeId = "test:0";
        var id = ScannerIdentity.Create(ScannerBackend.Sane, nativeId);
        var active = 0;

        async Task<T> Serialized<T>(Func<Task<T>> action, CancellationToken token = default)
        {
            await gate.WaitAsync(token);
            try
            {
                Assert.Equal(1, Interlocked.Increment(ref active));
                token.ThrowIfCancellationRequested();
                return await action();
            }
            finally
            {
                Interlocked.Decrement(ref active);
                gate.Release();
            }
        }

        async Task<string> Refresh(Func<Task>? afterExit = null)
        {
            await controller.GetDeviceList(new ScanOptions
            {
                Driver = Driver.Sane,
                SaneOptions = new SaneOptions { KeepInitialized = false }
            });
            if (afterExit is not null) await afterExit();
            var devices = await controller.GetDeviceList(Driver.Sane);
            return ScannerIdentity.Create(ScannerBackend.Sane,
                Assert.Single(devices.Where(device => device.ID == nativeId)).ID);
        }

        async Task<string> Acquire()
        {
            await using var pdf = await adapter.ScanAsync(id, ScanSource.Glass);
            var signature = new byte[5];
            Assert.Equal(5, await pdf.ReadAsync(signature.AsMemory()));
            return Encoding.ASCII.GetString(signature);
        }

        var baseline = await Serialized(() => adapter.GetScannersAsync());
        Assert.Contains(baseline.Scanners, scanner => scanner.Id == id);
        var acquisition = Serialized(async () =>
        {
            entered.SetResult();
            // Удерживаем всю составную операцию до завершения реального SDK scan.
            await release.Task;
            return await Acquire();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refreshStarted = false;
        var refresh = Serialized(async () =>
        {
            refreshStarted = true;
            return await Refresh();
        });
        var caps = Serialized(() => adapter.GetScannerCapabilitiesAsync(id));
        var listing = Serialized(() => adapter.GetScannersAsync());
        var cancelledRan = false;
        var cancelled = Serialized(() =>
        {
            cancelledRan = true;
            return Refresh();
        }, waitCancellation.Token);
        try
        {
            Assert.False(refreshStarted);
            Assert.False(refresh.IsCompleted);
            Assert.False(caps.IsCompleted);
            Assert.False(listing.IsCompleted);
            waitCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await cancelled; });
            Assert.False(cancelledRan);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal("%PDF-", await acquisition);
        Assert.Equal(id, await refresh);
        Assert.Equal(id, (await caps)!.Id);
        Assert.Contains((await listing).Scanners, scanner => scanner.Id == id);

        // Инъекция управляемого отказа между двумя стадиями refresh.
        var injected = new InvalidOperationException("Контрольный отказ между стадиями refresh");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Serialized(() => Refresh(() => Task.FromException(injected))));
        Assert.Same(injected, caught);
        Assert.Equal(id, await Serialized(() => Refresh()));
        Assert.Equal(id, await Serialized(() => Refresh()));

        // Обратное направление: старая сессия завершена, новая ещё не открыта.
        var afterExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRefresh = Serialized(() => Refresh(async () =>
        {
            afterExit.SetResult();
            await continueRefresh.Task;
        }));
        Task<string>? waitingScan = null;
        Task<ScannerDevice?>? waitingCaps = null;
        Task<ScannerDiscoveryResult>? waitingList = null;
        try
        {
            await afterExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            waitingScan = Serialized(Acquire);
            waitingCaps = Serialized(() => adapter.GetScannerCapabilitiesAsync(id));
            waitingList = Serialized(() => adapter.GetScannersAsync());
            Assert.False(waitingScan.IsCompleted);
            Assert.False(waitingCaps.IsCompleted);
            Assert.False(waitingList.IsCompleted);
        }
        finally
        {
            continueRefresh.TrySetResult();
            // До освобождения test resources дожидаемся всех начатых операций.
            await Task.WhenAll(new Task?[] { heldRefresh, waitingScan, waitingCaps, waitingList }
                .OfType<Task>());
        }
        Assert.Equal(id, await heldRefresh);
        Assert.Equal("%PDF-", await waitingScan!);
        Assert.Equal(id, (await waitingCaps!)!.Id);
        Assert.Contains((await waitingList!).Scanners, scanner => scanner.Id == id);
        output.WriteLine("B_GATE_REVERSE_MATRIX refresh_between_stages=PASS waiting_scan=PASS waiting_caps=PASS waiting_listing=PASS");
        Assert.Equal(0, active);
        output.WriteLine("B_GATE_MATRIX acquisition=PASS waiting_refresh=PASS caps=PASS listing=PASS waiting_cancel=PASS injected_failure=PASS retry=PASS identity=PASS");
    }

}

#pragma warning restore CA2252
