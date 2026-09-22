#!/usr/bin/env bash
set -euo pipefail

UPSTREAM_REPOSITORY="https://github.com/cyanfish/naps2.git"
UPSTREAM_COMMIT="450cba65aaffe6387041050a573051a64cd80fe9"
PACKAGE_ID="WebAssistant.NAPS2.Sdk"
PACKAGE_VERSION="1.3.0-webassistant.5.450cba65"
PACKAGE_FILE="$PACKAGE_ID.$PACKAGE_VERSION.nupkg"
WORKER_PACKAGE_ID="WebAssistant.NAPS2.Sdk.Worker.Win32"
WORKER_PACKAGE_VERSION="1.3.0-webassistant.2.450cba65"
WORKER_PACKAGE_FILE="$WORKER_PACKAGE_ID.$WORKER_PACKAGE_VERSION.nupkg"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
output_dir="$product_root/vendor/nuget"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

for command_name in git dotnet python3; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "Required command is unavailable: $command_name" >&2
        exit 1
    }
done

git -C "$work_dir" init -q
git -C "$work_dir" remote add origin "$UPSTREAM_REPOSITORY"
git -C "$work_dir" fetch -q --depth=1 origin "$UPSTREAM_COMMIT"
git -C "$work_dir" checkout -q --detach FETCH_HEAD

actual_commit="$(git -C "$work_dir" rev-parse HEAD)"
[[ "$actual_commit" == "$UPSTREAM_COMMIT" ]] || {
    echo "Unexpected upstream commit: $actual_commit" >&2
    exit 1
}

python3 - "$work_dir" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])


def replace_exact(path: pathlib.Path, expected: str, replacement: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    if text.count(expected) != 1:
        raise SystemExit(f"unexpected upstream layout in {path}: expected one exact match")
    path.write_text(text.replace(expected, replacement), encoding="utf-8")


targets = root / "NAPS2.Setup/targets/SdkPackageTargets.targets"
replace_exact(
    targets,
    "        <PackageVersion>1.3.0</PackageVersion>",
    "        <PackageVersion>1.3.0</PackageVersion>\n"
    "        <PackageId Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "WebAssistant.NAPS2.Sdk</PackageId>\n"
    "        <PackageVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "1.3.0-webassistant.5.450cba65</PackageVersion>\n"
    "        <PackageId Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk.Worker.Win32'\">"
    "WebAssistant.NAPS2.Sdk.Worker.Win32</PackageId>\n"
    "        <PackageVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk.Worker.Win32'\">"
    "1.3.0-webassistant.2.450cba65</PackageVersion>",
)

worker_package_project = root / "NAPS2.Sdk.Worker.Win32/NAPS2.Sdk.Worker.Win32.csproj"
replace_exact(
    worker_package_project,
    'PackagePath="build/NAPS2.Sdk.Worker.Win32.targets"',
    'PackagePath="build/WebAssistant.NAPS2.Sdk.Worker.Win32.targets"',
)

version_targets = root / "NAPS2.Setup/targets/VersionTargets.targets"
replace_exact(
    version_targets,
    "        <VersionName>8.3.0</VersionName>",
    "        <VersionName>8.3.0</VersionName>\n"
    "        <AssemblyVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">8.3.0.0</AssemblyVersion>\n"
    "        <FileVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">8.3.0.5</FileVersion>\n"
    "        <AssemblyVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk.Worker.Build'\">8.3.0.0</AssemblyVersion>\n"
    "        <FileVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk.Worker.Build'\">8.3.0.2</FileVersion>",
)

paper_source_caps = root / "NAPS2.Sdk/Scan/PaperSourceCaps.cs"
replace_exact(
    paper_source_caps,
    "    public bool CanCheckIfFeederHasPaper { get; init; }\n}",
    "    public bool CanCheckIfFeederHasPaper { get; init; }\n\n"
    "    /// <summary>\n"
    "    /// Whether paper is currently present in the feeder when that state can be read.\n"
    "    /// Null means the current state is unavailable or unknown.\n"
    "    /// </summary>\n"
    "    public bool? FeederHasPaper { get; init; }\n}",
)

wia_driver = root / "NAPS2.Sdk/Scan/Internal/Wia/WiaScanDriver.cs"
replace_exact(
    wia_driver,
    "                        SupportsDuplex = device.SupportsDuplex(),\n"
    "                        CanCheckIfFeederHasPaper = true\n",
    "                        SupportsDuplex = device.SupportsDuplex(),\n"
    "                        CanCheckIfFeederHasPaper = true,\n"
    "                        FeederHasPaper = device.SupportsFeeder() ? TryGetFeederHasPaper(device) : null\n",
)
replace_exact(
    wia_driver,
    "    private PerSourceCaps GetItemCaps(WiaDevice device, WiaItem item, bool flatbed)\n",
    "    private static bool? TryGetFeederHasPaper(WiaDevice device)\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            return device.FeederReady();\n"
    "        }\n"
    "        catch (WiaException)\n"
    "        {\n"
    "            return null;\n"
    "        }\n"
    "    }\n\n"
    "    private PerSourceCaps GetItemCaps(WiaDevice device, WiaItem item, bool flatbed)\n",
)

twain_driver = root / "NAPS2.Sdk/Scan/Internal/Twain/LocalTwainController.cs"
replace_exact(
    twain_driver,
    "                            CanCheckIfFeederHasPaper =\n"
    "                                ds.Capabilities.CapAutomaticSenseMedium.IsSupported ||\n"
    "                                ds.Capabilities.CapFeederLoaded.IsSupported\n",
    "                            CanCheckIfFeederHasPaper =\n"
    "                                ds.Capabilities.CapAutomaticSenseMedium.IsSupported ||\n"
    "                                ds.Capabilities.CapFeederLoaded.IsSupported,\n"
    "                            FeederHasPaper = TryGetFeederHasPaper(ds)\n",
)
replace_exact(
    twain_driver,
    "    private PerSourceCaps GetPerSourceCaps(DataSource ds)\n",
    "    private bool? TryGetFeederHasPaper(DataSource ds)\n"
    "    {\n"
    "        var feederLoaded = ds.Capabilities.CapFeederLoaded;\n"
    "        if (!feederLoaded.IsSupported)\n"
    "        {\n"
    "            return null;\n"
    "        }\n\n"
    "        try\n"
    "        {\n"
    "            return feederLoaded.GetCurrent() == BoolType.True;\n"
    "        }\n"
    "        catch (Exception e)\n"
    "        {\n"
    "            _logger.LogDebug(e, \"Could not read TWAIN feeder-loaded state\");\n"
    "            return null;\n"
    "        }\n"
    "    }\n\n"
    "    private PerSourceCaps GetPerSourceCaps(DataSource ds)\n",
)


replace_exact(
    twain_driver,
    '''    private static readonly Once TwainDsmSetup = new(() =>
    {
        var twainDsmPath = NativeLibrary.FindLibraryPath("twaindsm.dll");
        PlatformCompat.System.LoadLibrary(twainDsmPath);
        PlatformInfo.Current.NewDsmPath = twainDsmPath;
    });
''',
    '''    private static string? _newDsmPath;

    private static readonly Once TwainDsmSetup = new(() =>
    {
        var twainDsmPath = NativeLibrary.FindLibraryPath("twaindsm.dll");
        PlatformCompat.System.LoadLibrary(twainDsmPath);
        PlatformInfo.Current.NewDsmPath = twainDsmPath;
        _newDsmPath = twainDsmPath;
    });
''',
)
replace_exact(
    twain_driver,
    '''    public Task<ScanCaps> GetCaps(ScanOptions options)
    {
        if (options.TwainOptions.Dsm != TwainDsm.Old)
''',
    '''    public Task<ScanCaps> GetCaps(ScanOptions options)
    {
        Trace($"twain.dsm event=request requestedDsm={options.TwainOptions.Dsm}");
        if (options.TwainOptions.Dsm != TwainDsm.Old)
''',
)
replace_exact(
    twain_driver,
    '''    private ScanCaps InternalGetCaps(ScanOptions options)
    {
        PlatformInfo.Current.PreferNewDSM = options.TwainOptions.Dsm != TwainDsm.Old;
        var session = new TwainSession(TwainAppId);
        using var handleManager = TwainHandleManager.Factory();
        session.Open(handleManager.CreateMessageLoopHook());
''',
    '''    private ScanCaps InternalGetCaps(ScanOptions options)
    {
        PlatformInfo.Current.PreferNewDSM = options.TwainOptions.Dsm != TwainDsm.Old;
        var effectiveDsm = PlatformInfo.Current.PreferNewDSM ? "new" : "old";
        var resolvedPath = PlatformInfo.Current.PreferNewDSM
            ? _newDsmPath ?? "<unknown>"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "twain_32.dll");
        Trace(
            $"twain.dsm event=effective requestedDsm={options.TwainOptions.Dsm} resolvedDsm={effectiveDsm} effectiveDsm={effectiveDsm} resolvedPath={SanitizeDiagnosticValue(resolvedPath)}");

        var session = new TwainSession(TwainAppId);
        using var handleManager = TwainHandleManager.Factory();
        TraceStage("sessionOpen", () => session.Open(handleManager.CreateMessageLoopHook()));
        TraceTwainModules("sessionOpen");
''',
)
replace_exact(
    twain_driver,
    '''                var rc = ds.Open();
                if (rc != ReturnCode.Success)
''',
    '''                var rc = TraceStage("dsOpen", ds.Open);
                TraceTwainModules("dsOpen");
                if (rc != ReturnCode.Success)
''',
)
replace_exact(
    twain_driver,
    '''                    var feederCap = ds.Capabilities.CapFeederEnabled;

                    feederCap.SetValue(BoolType.False);
                    bool supportsFlatbed = feederCap.GetCurrent() == BoolType.False;
                    var flatbedCaps = supportsFlatbed ? GetPerSourceCaps(ds) : null;

                    feederCap.SetValue(BoolType.True);
                    bool supportsFeeder = feederCap.GetCurrent() == BoolType.True;
                    var feederCaps = supportsFeeder ? GetPerSourceCaps(ds) : null;

                    bool supportsDuplex = supportsFeeder && ds.Capabilities.CapDuplex.GetCurrent() != Duplex.None;
''',
    '''                    var feederCap = ds.Capabilities.CapFeederEnabled;

                    TraceStage("feederSetFalse", () => { feederCap.SetValue(BoolType.False); });
                    bool supportsFlatbed = TraceStage(
                        "feederGetFalse",
                        () => feederCap.GetCurrent() == BoolType.False);
                    var flatbedCaps = TraceStage<PerSourceCaps?>(
                        "flatbedCaps",
                        () => supportsFlatbed ? GetPerSourceCaps(ds) : null);

                    TraceStage("feederSetTrue", () => { feederCap.SetValue(BoolType.True); });
                    bool supportsFeeder = TraceStage(
                        "feederGetTrue",
                        () => feederCap.GetCurrent() == BoolType.True);
                    var feederCaps = TraceStage<PerSourceCaps?>(
                        "feederCaps",
                        () => supportsFeeder ? GetPerSourceCaps(ds) : null);

                    bool supportsDuplex = TraceStage(
                        "duplex",
                        () => supportsFeeder && ds.Capabilities.CapDuplex.GetCurrent() != Duplex.None);
''',
)
replace_exact(
    twain_driver,
    '''                    return new ScanCaps
                    {
                        MetadataCaps = new MetadataCaps
                        {
                            Manufacturer = ds.Manufacturer,
                            Model = ds.Name,
                            SerialNumber = ds.Capabilities.CapSerialNumber.GetCurrent()
                        },
''',
    '''                    var metadata = TraceStage(
                        "metadata",
                        () => new MetadataCaps
                        {
                            Manufacturer = ds.Manufacturer,
                            Model = ds.Name,
                            SerialNumber = ds.Capabilities.CapSerialNumber.GetCurrent()
                        });

                    return new ScanCaps
                    {
                        MetadataCaps = metadata,
''',
)
replace_exact(
    twain_driver,
    "    private PerSourceCaps GetPerSourceCaps(DataSource ds)\n",
    '''    private static T TraceStage<T>(string stage, Func<T> action)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Trace($"twain.getCaps stage={stage} event=begin");
        try
        {
            var result = action();
            Trace(
                $"twain.getCaps stage={stage} event=end outcome=success durationMs={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
            return result;
        }
        catch (Exception exception)
        {
            Trace(
                $"twain.getCaps stage={stage} event=end outcome=failure durationMs={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds} exceptionType={exception.GetType().Name} hresult=0x{unchecked((uint)exception.HResult):X8}");
            throw;
        }
    }

    private static void TraceStage(string stage, Action action)
    {
        TraceStage<object?>(stage, () =>
        {
            action();
            return null;
        });
    }

    private static void TraceTwainModules(string phase)
    {
        try
        {
            var found = false;
            foreach (System.Diagnostics.ProcessModule module in
                     System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                var modulePath = module.FileName;
                var moduleName = Path.GetFileName(modulePath);
                var isDsm =
                    string.Equals(moduleName, "twaindsm.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(moduleName, "twain_32.dll", StringComparison.OrdinalIgnoreCase);
                var isVendor =
                    modulePath.Contains(
                        $"{Path.DirectorySeparatorChar}twain_32{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(moduleName, "twain_32.dll", StringComparison.OrdinalIgnoreCase);

                if (!isDsm && !isVendor)
                {
                    continue;
                }

                found = true;
                var version = System.Diagnostics.FileVersionInfo
                    .GetVersionInfo(modulePath)
                    .FileVersion ?? "";
                using var stream = File.OpenRead(modulePath);
                var sha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(stream));

                Trace(
                    $"twain.module phase={phase} observation=loaded moduleName={SanitizeDiagnosticValue(moduleName)} modulePath={SanitizeDiagnosticValue(modulePath)} fileVersion={SanitizeDiagnosticValue(version)} sha256={sha256}");
            }

            if (!found)
            {
                Trace($"twain.module phase={phase} observation=notLoaded");
            }
        }
        catch (Exception exception)
        {
            Trace(
                $"twain.module phase={phase} observation=notObservable exceptionType={exception.GetType().Name} hresult=0x{unchecked((uint)exception.HResult):X8}");
        }
    }

    private static void Trace(string value)
    {
        try
        {
            Console.Error.WriteLine($"WA_DIAG|{value}");
        }
        catch
        {
        }
    }

    private static string SanitizeDiagnosticValue(string value) =>
        new(
            value
                .Where(character => !char.IsControl(character) && character != '|')
                .Take(512)
                .ToArray());

    private PerSourceCaps GetPerSourceCaps(DataSource ds)
''',
)

worker_context = root / "NAPS2.Sdk/Remoting/Worker/WorkerContext.cs"
replace_exact(
    worker_context,
    "    private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(60);\n\n"
    "    private readonly ILogger _logger;\n"
    "    private bool _stopped;\n",
    "    private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(10);\n"
    "    private static readonly TimeSpan WorkerKillTimeout = TimeSpan.FromSeconds(2);\n\n"
    "    private readonly ILogger _logger;\n"
    "    private readonly object _stopLock = new();\n"
    "    private Task? _stopTask;\n",
)
replace_exact(
    worker_context,
    '''    public async Task Stop()\n    {\n        if (_stopped) return;\n        _stopped = true;\n\n        // Try to cleanly stop the worker\n        Task.Run(() =>\n        {\n            try\n            {\n                Service.StopWorker();\n            }\n            catch (RpcException e) when (e.Status.StatusCode == StatusCode.Unavailable)\n            {\n                // This can happen normally if the system is shutting down (and terminated the worker processes) so we\n                // don't log as an error.\n                _logger.LogDebug("Could not stop the worker process. It may have crashed.");\n            }\n            catch (Exception e)\n            {\n                _logger.LogError(e, "Error stopping worker");\n            }\n        }).AssertNoAwait();\n\n        // Wait for either the worker process to close or for our timeout\n        await Task.WhenAny(Process.WaitForExitAsync(), Task.Delay(WorkerStopTimeout)).ConfigureAwait(false);\n\n        // If the worker process still hasn't closed we kill it now\n        if (!Process.HasExited)\n        {\n            _logger.LogError("Killing unresponsive worker");\n            try\n            {\n                Process.Kill();\n            }\n            catch (Exception e)\n            {\n                _logger.LogError(e, "Error killing unresponsive worker");\n            }\n        }\n    }\n\n    public void Dispose()\n    {\n        Stop().AssertNoAwait();\n    }\n''',
    '''    public Task Stop()\n    {\n        lock (_stopLock)\n        {\n            return _stopTask ??= StopCoreAsync();\n        }\n    }\n\n    private async Task StopCoreAsync()\n    {\n        _ = Task.Run(() =>\n        {\n            try\n            {\n                Service.StopWorker();\n            }\n            catch (RpcException e) when (e.Status.StatusCode == StatusCode.Unavailable)\n            {\n                _logger.LogDebug("Could not stop the worker process. It may have crashed.");\n            }\n            catch (Exception e)\n            {\n                _logger.LogError(e, "Error stopping worker");\n            }\n        });\n\n        if (!Process.HasExited)\n        {\n            await Task.WhenAny(Process.WaitForExitAsync(), Task.Delay(WorkerStopTimeout)).ConfigureAwait(false);\n        }\n\n        if (!Process.HasExited)\n        {\n            _logger.LogError("Killing unresponsive worker");\n            try\n            {\n                Process.Kill();\n            }\n            catch (InvalidOperationException) when (Process.HasExited)\n            {\n            }\n            catch (Exception e)\n            {\n                _logger.LogError(e, "Error killing unresponsive worker");\n                throw;\n            }\n\n            if (!Process.HasExited)\n            {\n                await Task.WhenAny(Process.WaitForExitAsync(), Task.Delay(WorkerKillTimeout)).ConfigureAwait(false);\n            }\n        }\n\n        if (!Process.HasExited)\n        {\n            throw new TimeoutException($"Worker process {Process.Id} did not exit after termination.");\n        }\n    }\n\n    public void Dispose()\n    {\n        Stop().GetAwaiter().GetResult();\n    }\n''',
)

worker_factory_interface = root / "NAPS2.Sdk/Remoting/Worker/IWorkerFactory.cs"
replace_exact(
    worker_factory_interface,
    "    void StopSpareWorkers();\n",
    "    void StopSpareWorkers();\n"
    "    Task ShutdownAsync();\n",
)

worker_factory = root / "NAPS2.Sdk/Remoting/Worker/WorkerFactory.cs"
replace_exact(
    worker_factory,
    "    private readonly Dictionary<string, string> _environmentVariables;\n\n"
    "    private Dictionary<WorkerType, BlockingCollection<WorkerContext>>? _workerQueues;\n",
    "    private readonly Dictionary<string, string> _environmentVariables;\n"
    "    private readonly object _lifecycleLock = new();\n"
    "    private readonly HashSet<WorkerContext> _workers = new();\n"
    "    private readonly HashSet<Task> _workerStarts = new();\n"
    "    private bool _shutdownStarted;\n"
    "    private Task? _shutdownTask;\n\n"
    "    private Dictionary<WorkerType, BlockingCollection<WorkerContext>>? _workerQueues;\n",
)
replace_exact(
    worker_factory,
    '''        // TODO: Since we set RedirectStandardOutput, we should consume stdout to prevent the buffer from filling up and\n        // stalling the worker process\n        var readyStr = proc.StandardOutput.ReadLine();\n        if (readyStr?.Trim() == "error")\n        {\n            var error = proc.StandardOutput.ReadToEnd();\n            throw new InvalidOperationException($"The worker could not start due to an error: {error}");\n        }\n\n        if (readyStr?.Trim() != "ready")\n        {\n            throw new InvalidOperationException("Unknown problem starting the worker.");\n        }\n\n        return proc;\n''',
    '''        try\n        {\n            // TODO: Since we set RedirectStandardOutput, we should consume stdout to prevent the buffer from filling up and\n            // stalling the worker process\n            var readyStr = proc.StandardOutput.ReadLine();\n            if (readyStr?.Trim() == "error")\n            {\n                var error = proc.StandardOutput.ReadToEnd();\n                throw new InvalidOperationException($"The worker could not start due to an error: {error}");\n            }\n\n            if (readyStr?.Trim() != "ready")\n            {\n                throw new InvalidOperationException("Unknown problem starting the worker.");\n            }\n\n            return proc;\n        }\n        catch\n        {\n            try\n            {\n                if (!proc.HasExited)\n                {\n                    proc.Kill();\n                    if (!proc.WaitForExit(2_000))\n                    {\n                        throw new TimeoutException($"Worker process {proc.Id} did not exit after failed startup.");\n                    }\n                }\n            }\n            catch (InvalidOperationException) when (proc.HasExited)\n            {\n            }\n            throw;\n        }\n''',
)
replace_exact(
    worker_factory,
    '''    private void StartWorkerService(ScanningContext scanningContext, WorkerType workerType, bool spare)\n    {\n        Task.Run(() =>\n        {\n            try\n            {\n                var proc = StartWorkerProcess(workerType);\n                var options = new NamedPipeChannelOptions\n                {\n                    ConnectionTimeout = PIPE_CONNECTION_TIMEOUT\n                };\n                var channel = new NamedPipeChannel(".", string.Format(PIPE_NAME_FORMAT, proc.Id), options);\n                _workerQueues![workerType]\n                    .Add(new WorkerContext(scanningContext, workerType, new WorkerServiceAdapter(channel), proc));\n            }\n            catch (Exception ex)\n            {\n                // If we're just starting a spare worker, don't log errors (e.g. if we're using the SDK and don't even\n                // need a worker)\n                if (!spare)\n                {\n                    scanningContext.Logger.LogError(ex, "Could not start worker");\n                }\n            }\n        });\n    }\n''',
    '''    private void StartWorkerService(ScanningContext scanningContext, WorkerType workerType, bool spare)\n    {\n        Task startTask;\n        lock (_lifecycleLock)\n        {\n            if (_shutdownStarted)\n            {\n                if (spare) return;\n                throw new ObjectDisposedException(nameof(WorkerFactory));\n            }\n\n            startTask = Task.Run(async () =>\n            {\n                try\n                {\n                    var proc = StartWorkerProcess(workerType);\n                    var options = new NamedPipeChannelOptions\n                    {\n                        ConnectionTimeout = PIPE_CONNECTION_TIMEOUT\n                    };\n                    var channel = new NamedPipeChannel(".", string.Format(PIPE_NAME_FORMAT, proc.Id), options);\n                    var worker = new WorkerContext(\n                        scanningContext, workerType, new WorkerServiceAdapter(channel), proc);\n\n                    bool publish;\n                    lock (_lifecycleLock)\n                    {\n                        _workers.Add(worker);\n                        publish = !_shutdownStarted;\n                        if (publish)\n                        {\n                            _workerQueues![workerType].Add(worker);\n                        }\n                    }\n\n                    if (!publish)\n                    {\n                        await worker.Stop().ConfigureAwait(false);\n                    }\n                }\n                catch (Exception ex)\n                {\n                    if (!spare)\n                    {\n                        scanningContext.Logger.LogError(ex, "Could not start worker");\n                    }\n                }\n            });\n            _workerStarts.Add(startTask);\n        }\n\n        _ = startTask.ContinueWith(\n            completed =>\n            {\n                lock (_lifecycleLock)\n                {\n                    _workerStarts.Remove(completed);\n                }\n            },\n            TaskScheduler.Default);\n    }\n''',
)
replace_exact(
    worker_factory,
    '''    private WorkerContext NextWorker(ScanningContext scanningContext, WorkerType workerType)\n    {\n        StartWorkerService(scanningContext, workerType, false);\n        if (!_workerQueues![workerType]!.TryTake(out var worker, TAKE_WORKER_TIMEOUT))\n        {\n            throw new InvalidOperationException("Could not start a worker process; see logs for details");\n        }\n        return worker;\n    }\n''',
    '''    private WorkerContext NextWorker(ScanningContext scanningContext, WorkerType workerType)\n    {\n        ThrowIfShutdownStarted();\n        StartWorkerService(scanningContext, workerType, false);\n        if (!_workerQueues![workerType]!.TryTake(out var worker, TAKE_WORKER_TIMEOUT))\n        {\n            ThrowIfShutdownStarted();\n            throw new InvalidOperationException("Could not start a worker process; see logs for details");\n        }\n\n        lock (_lifecycleLock)\n        {\n            if (!_shutdownStarted)\n            {\n                return worker;\n            }\n        }\n\n        worker.Stop().GetAwaiter().GetResult();\n        throw new ObjectDisposedException(nameof(WorkerFactory));\n    }\n''',
)
replace_exact(
    worker_factory,
    '''    public WorkerContext Create(ScanningContext scanningContext, WorkerType workerType)\n    {\n        if (_workerQueues == null)\n        {\n            throw new InvalidOperationException("WorkerFactory has not been initialized");\n        }\n        var worker = NextWorker(scanningContext, workerType);\n        worker.Service.Init(scanningContext.FileStorageManager?.FolderPath);\n        return worker;\n    }\n''',
    '''    public WorkerContext Create(ScanningContext scanningContext, WorkerType workerType)\n    {\n        if (_workerQueues == null)\n        {\n            throw new InvalidOperationException("WorkerFactory has not been initialized");\n        }\n        var worker = NextWorker(scanningContext, workerType);\n        worker.Service.Init(scanningContext.FileStorageManager?.FolderPath);\n\n        lock (_lifecycleLock)\n        {\n            if (!_shutdownStarted)\n            {\n                return worker;\n            }\n        }\n\n        worker.Stop().GetAwaiter().GetResult();\n        throw new ObjectDisposedException(nameof(WorkerFactory));\n    }\n''',
)
replace_exact(
    worker_factory,
    '''    public void RecreateSpareWorkers()\n    {\n        if (_workerQueues == null) return;\n        foreach (var queue in _workerQueues.Values)\n        {\n            if (queue.TryTake(out var worker))\n            {\n                worker.Stop().AssertNoAwait();\n                StartWorkerService(worker.ScanningContext, worker.Type, true);\n            }\n        }\n    }\n''',
    '''    public void RecreateSpareWorkers()\n    {\n        if (_workerQueues == null) return;\n        lock (_lifecycleLock)\n        {\n            if (_shutdownStarted) return;\n        }\n        foreach (var queue in _workerQueues.Values)\n        {\n            if (queue.TryTake(out var worker))\n            {\n                _ = worker.Stop();\n                StartWorkerService(worker.ScanningContext, worker.Type, true);\n            }\n        }\n    }\n''',
)
replace_exact(
    worker_factory,
    '''    public void StopSpareWorkers()\n    {\n        if (_workerQueues == null) return;\n        var stopTasks = new List<Task>();\n        foreach (var queue in _workerQueues.Values)\n        {\n            while (queue.TryTake(out var worker))\n            {\n                stopTasks.Add(worker.Stop());\n            }\n        }\n        Task.WhenAll(stopTasks).Wait();\n    }\n''',
    '''    public void StopSpareWorkers()\n    {\n        if (_workerQueues == null) return;\n        var stopTasks = new List<Task>();\n        foreach (var queue in _workerQueues.Values)\n        {\n            while (queue.TryTake(out var worker))\n            {\n                stopTasks.Add(worker.Stop());\n            }\n        }\n        Task.WhenAll(stopTasks).GetAwaiter().GetResult();\n    }\n\n    public Task ShutdownAsync()\n    {\n        lock (_lifecycleLock)\n        {\n            if (_shutdownTask != null)\n            {\n                return _shutdownTask;\n            }\n\n            _shutdownStarted = true;\n            _shutdownTask = Task.Run(StopAllWorkersAsync);\n            return _shutdownTask;\n        }\n    }\n\n    private async Task StopAllWorkersAsync()\n    {\n        Task[] startTasks;\n        lock (_lifecycleLock)\n        {\n            startTasks = _workerStarts.ToArray();\n        }\n        await Task.WhenAll(startTasks).ConfigureAwait(false);\n\n        WorkerContext[] workers;\n        lock (_lifecycleLock)\n        {\n            workers = _workers.ToArray();\n        }\n\n        await Task.WhenAll(workers.Select(worker => worker.Stop())).ConfigureAwait(false);\n\n        var aliveWorkers = workers.Where(worker => !worker.Process.HasExited).ToArray();\n        if (aliveWorkers.Length > 0)\n        {\n            throw new TimeoutException(\n                $"{aliveWorkers.Length} worker process(es) remained alive after shutdown.");\n        }\n\n        lock (_lifecycleLock)\n        {\n            foreach (var worker in workers)\n            {\n                _workers.Remove(worker);\n            }\n            if (_workerQueues != null)\n            {\n                foreach (var queue in _workerQueues.Values)\n                {\n                    while (queue.TryTake(out _))\n                    {\n                    }\n                }\n            }\n        }\n    }\n\n    private void ThrowIfShutdownStarted()\n    {\n        lock (_lifecycleLock)\n        {\n            if (_shutdownStarted)\n            {\n                throw new ObjectDisposedException(nameof(WorkerFactory));\n            }\n        }\n    }\n''',
)


replace_exact(
    worker_factory,
    '''                Arguments = $"{parentId}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
''',
    '''                Arguments = $"{parentId}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
''',
)
replace_exact(
    worker_factory,
    '''                Arguments = $"worker {parentId}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
''',
    '''                Arguments = $"worker {parentId}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
''',
)
replace_exact(
    worker_factory,
    "    private void StartWorkerService(ScanningContext scanningContext, WorkerType workerType, bool spare)\n",
    '''    private static void StartWorkerDiagnosticReader(
        ScanningContext scanningContext,
        WorkerType workerType,
        Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        return;
                    }
                    if (!line.StartsWith("WA_DIAG|", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    scanningContext.Logger.LogDebug(
                        "naps2.worker.diagnostic workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType} {WorkerDiagnostic}",
                        process.Id,
                        Environment.ProcessId,
                        workerType,
                        line["WA_DIAG|".Length..]);
                }
            }
            catch (Exception exception)
            {
                scanningContext.Logger.LogDebug(
                    "naps2.worker.diagnostic workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType} observation=notObservable exceptionType={ExceptionType} hresult={HResult}",
                    process.Id,
                    Environment.ProcessId,
                    workerType,
                    exception.GetType().Name,
                    $"0x{unchecked((uint)exception.HResult):X8}");
            }
        });
    }

    private void StartWorkerService(ScanningContext scanningContext, WorkerType workerType, bool spare)
''',
)
replace_exact(
    worker_factory,
    '''                    var proc = StartWorkerProcess(workerType);
                    var options = new NamedPipeChannelOptions
''',
    '''                    var proc = StartWorkerProcess(workerType);
                    StartWorkerDiagnosticReader(scanningContext, workerType, proc);
                    var options = new NamedPipeChannelOptions
''',
)
replace_exact(
    worker_factory,
    '''        var worker = NextWorker(scanningContext, workerType);
        worker.Service.Init(scanningContext.FileStorageManager?.FolderPath);

        lock (_lifecycleLock)
''',
    '''        scanningContext.Logger.LogDebug(
            "worker.acquire event=begin workerType={WorkerType}",
            workerType);
        var worker = NextWorker(scanningContext, workerType);
        worker.Service.Init(scanningContext.FileStorageManager?.FolderPath);
        scanningContext.Logger.LogDebug(
            "worker.acquire event=end workerType={WorkerType} workerPid={WorkerPid} parentPid={ParentPid} workerArchitecture={WorkerArchitecture}",
            workerType,
            worker.Process.Id,
            Environment.ProcessId,
            workerType == WorkerType.WinX86
                ? "x86"
                : Environment.Is64BitProcess ? "x64" : "x86");

        lock (_lifecycleLock)
''',
)
replace_exact(
    worker_context,
    '''    private async Task StopCoreAsync()
    {
        _ = Task.Run(() =>
''',
    '''    private async Task StopCoreAsync()
    {
        _logger.LogDebug(
            "worker.release event=begin workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType}",
            Process.Id,
            Environment.ProcessId,
            Type);

        _ = Task.Run(() =>
''',
)
replace_exact(
    worker_context,
    '''        if (!Process.HasExited)
        {
            throw new TimeoutException($"Worker process {Process.Id} did not exit after termination.");
        }
    }
''',
    '''        if (!Process.HasExited)
        {
            _logger.LogError(
                "worker.exit event=end outcome=timeout workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType}",
                Process.Id,
                Environment.ProcessId,
                Type);
            _logger.LogDebug(
                "worker.release event=end outcome=failure workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType}",
                Process.Id,
                Environment.ProcessId,
                Type);
            throw new TimeoutException($"Worker process {Process.Id} did not exit after termination.");
        }

        _logger.LogDebug(
            "worker.exit event=end outcome=success workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType} exitCode={ExitCode}",
            Process.Id,
            Environment.ProcessId,
            Type,
            Process.ExitCode);
        _logger.LogDebug(
            "worker.release event=end outcome=success workerPid={WorkerPid} parentPid={ParentPid} workerType={WorkerType}",
            Process.Id,
            Environment.ProcessId,
            Type);
    }
''',
)

scanning_context = root / "NAPS2.Sdk/Scan/ScanningContext.cs"
replace_exact(
    scanning_context,
    "    private readonly ProcessedImageOwner _processedImageOwner = new();\n",
    "    private readonly ProcessedImageOwner _processedImageOwner = new();\n"
    "    private readonly object _shutdownLock = new();\n"
    "    private Task? _shutdownTask;\n",
)
replace_exact(
    scanning_context,
    '''    public void Dispose()\n    {\n        _processedImageOwner.Dispose();\n        FileStorageManager?.Dispose();\n    }\n''',
    '''    public Task ShutdownAsync()\n    {\n        lock (_shutdownLock)\n        {\n            return _shutdownTask ??= ShutdownCoreAsync();\n        }\n    }\n\n    private async Task ShutdownCoreAsync()\n    {\n        if (WorkerFactory != null)\n        {\n            await WorkerFactory.ShutdownAsync().ConfigureAwait(false);\n        }\n        _processedImageOwner.Dispose();\n        FileStorageManager?.Dispose();\n    }\n\n    public void Dispose()\n    {\n        ShutdownAsync().GetAwaiter().GetResult();\n    }\n''',
)
PY

project="$work_dir/NAPS2.Sdk/NAPS2.Sdk.csproj"
package_path="$output_dir/$PACKAGE_FILE"
worker_project="$work_dir/NAPS2.Sdk.Worker.Build/NAPS2.Sdk.Worker.Build.csproj"
worker_package_project="$work_dir/NAPS2.Sdk.Worker.Win32/NAPS2.Sdk.Worker.Win32.csproj"
worker_package_path="$output_dir/$WORKER_PACKAGE_FILE"
path_map="$work_dir=/src/naps2"
mkdir -p -- "$output_dir"
rm -f -- "$package_path" "$worker_package_path"

dotnet build "$project" \
    --configuration Release \
    --property:TargetFrameworks=net10.0 \
    --property:GeneratePackageOnBuild=false \
    --property:PathMap="$path_map" \
    --property:DebugType=None \
    --property:DebugSymbols=false

dotnet pack "$project" \
    --configuration Release \
    --no-build \
    --property:TargetFrameworks=net10.0 \
    --property:DebugType=None \
    --property:DebugSymbols=false \
    --property:PackageOutputPath="$output_dir"

[[ -f "$package_path" ]] || {
    echo "Expected package was not produced: $package_path" >&2
    exit 1
}

dotnet publish "$worker_project" \
    --configuration Release \
    --property:PathMap="$path_map" \
    --property:ContinuousIntegrationBuild=true \
    --property:Deterministic=true \
    --property:DebugType=None \
    --property:DebugSymbols=false \
    --property:GeneratePackageOnBuild=false

worker_exe="$work_dir/NAPS2.Sdk.Worker.Build/bin/Release/net10.0/win-x86/publish/NAPS2.Worker.exe"
[[ -f "$worker_exe" ]] || {
    echo "Expected source-aligned worker was not produced: $worker_exe" >&2
    exit 1
}

dotnet pack "$worker_package_project" \
    --configuration Release \
    --property:TargetFrameworks=net10.0 \
    --property:GeneratePackageOnBuild=false \
    --property:DebugType=None \
    --property:DebugSymbols=false \
    --property:PackageOutputPath="$output_dir"

[[ -f "$worker_package_path" ]] || {
    echo "Expected worker package was not produced: $worker_package_path" >&2
    exit 1
}

python3 - "$package_path" <<'PY'
import hashlib
import os
import pathlib
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
temporary = package.with_name(package.name + ".canonical.tmp")
fixed_timestamp = (1980, 1, 1, 0, 0, 0)

with zipfile.ZipFile(package, "r") as source:
    entries = [(entry.filename, entry.is_dir(), source.read(entry)) for entry in source.infolist()]

names = [name for name, _, _ in entries]
if len(names) != len(set(names)):
    raise SystemExit("refusing to canonicalize package with duplicate ZIP entry names")

for name, is_directory, data in sorted(entries, key=lambda item: item[0]):
    kind = "dir" if is_directory else "file"
    digest = hashlib.sha256(data).hexdigest()
    print(f"ENTRY_SHA256 {digest} {len(data)} {kind} {name}")

try:
    with zipfile.ZipFile(
        temporary,
        "w",
        compression=zipfile.ZIP_STORED,
        allowZip64=False,
    ) as target:
        target.comment = b""
        for name, is_directory, data in sorted(entries, key=lambda item: item[0]):
            info = zipfile.ZipInfo(name, date_time=fixed_timestamp)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.create_version = 20
            info.extract_version = 20
            info.internal_attr = 0
            info.external_attr = (
                ((0o40755 << 16) | 0x10)
                if is_directory
                else (0o100644 << 16)
            )
            info.extra = b""
            info.comment = b""
            target.writestr(info, data)

    os.replace(temporary, package)
finally:
    temporary.unlink(missing_ok=True)
PY

python3 - "$worker_package_path" <<'PY'
import hashlib
import os
import pathlib
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
temporary = package.with_name(package.name + ".canonical.tmp")
fixed_timestamp = (1980, 1, 1, 0, 0, 0)

with zipfile.ZipFile(package, "r") as source:
    entries = [(entry.filename, entry.is_dir(), source.read(entry)) for entry in source.infolist()]

names = [name for name, _, _ in entries]
if len(names) != len(set(names)):
    raise SystemExit("refusing to canonicalize package with duplicate ZIP entry names")

for name, is_directory, data in sorted(entries, key=lambda item: item[0]):
    kind = "dir" if is_directory else "file"
    digest = hashlib.sha256(data).hexdigest()
    print(f"ENTRY_SHA256 {digest} {len(data)} {kind} {name}")

try:
    with zipfile.ZipFile(
        temporary,
        "w",
        compression=zipfile.ZIP_STORED,
        allowZip64=False,
    ) as target:
        target.comment = b""
        for name, is_directory, data in sorted(entries, key=lambda item: item[0]):
            info = zipfile.ZipInfo(name, date_time=fixed_timestamp)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.create_version = 20
            info.extract_version = 20
            info.internal_attr = 0
            info.external_attr = (
                ((0o40755 << 16) | 0x10)
                if is_directory
                else (0o100644 << 16)
            )
            info.extra = b""
            info.comment = b""
            target.writestr(info, data)

    os.replace(temporary, package)
finally:
    temporary.unlink(missing_ok=True)
PY

echo "Rebuilt SDK: $package_path"
echo "Rebuilt Win32 worker: $worker_package_path"
