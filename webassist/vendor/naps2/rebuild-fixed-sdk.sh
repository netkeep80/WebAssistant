#!/usr/bin/env bash
set -euo pipefail

UPSTREAM_REPOSITORY="https://github.com/cyanfish/naps2.git"
UPSTREAM_COMMIT="450cba65aaffe6387041050a573051a64cd80fe9"
PACKAGE_ID="WebAssistant.NAPS2.Sdk"
PACKAGE_VERSION="1.3.0-webassistant.3.450cba65"
PACKAGE_FILE="$PACKAGE_ID.$PACKAGE_VERSION.nupkg"

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
    "1.3.0-webassistant.3.450cba65</PackageVersion>",
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
mkdir -p -- "$output_dir"
rm -f -- "$output_dir/$PACKAGE_FILE"

dotnet build "$project" \
    --configuration Release \
    --property:TargetFrameworks=net10.0 \
    --property:GeneratePackageOnBuild=false

dotnet pack "$project" \
    --configuration Release \
    --no-build \
    --property:TargetFrameworks=net10.0 \
    --property:PackageOutputPath="$output_dir"

[[ -f "$output_dir/$PACKAGE_FILE" ]] || {
    echo "Expected package was not produced: $output_dir/$PACKAGE_FILE" >&2
    exit 1
}

echo "Rebuilt: $output_dir/$PACKAGE_FILE"
