namespace WebAssistant.UpgradePreflight;

internal sealed record UpgradePreflightPolicy(
    TimeSpan ServiceStopTimeout,
    TimeSpan WorkerGraceTimeout,
    TimeSpan PostTerminateTimeout,
    TimeSpan PollInterval);

internal sealed class UpgradePreflightException : Exception
{
    internal UpgradePreflightException(string message)
        : base(message)
    {
    }
}

internal sealed class UpgradePreflightOrchestrator
{
    private static readonly TimeSpan DefaultConvergenceSlice = TimeSpan.FromMilliseconds(100);

    private readonly IUpgradeEnvironment environment;
    private readonly UpgradePreflightPolicy policy;

    internal UpgradePreflightOrchestrator(
        IUpgradeEnvironment environment,
        UpgradePreflightPolicy policy)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var service = environment.TryGetWebAssistantService();
        if (service is null)
        {
            return;
        }

        var servicePath = NormalizeWindowsPath(service.ExecutablePath);
        var installRoot = GetWindowsDirectory(servicePath);
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new UpgradePreflightException(
                "WebAssistant service executable path does not have a safe install root.");
        }

        var allowedWorkerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeWindowsPath($"{installRoot}\\NAPS2.Worker.exe"),
            NormalizeWindowsPath($"{installRoot}\\lib\\NAPS2.Worker.exe")
        };

        if (service.Process is null)
        {
            if (service.State != ServiceState.Stopped)
            {
                throw new UpgradePreflightException(
                    "WebAssistant service has no process identity while it is not stopped.");
            }

            var ambiguousPackageWorkers = environment
                .SnapshotProcesses()
                .Where(process =>
                    allowedWorkerPaths.Contains(NormalizeWindowsPath(process.ImagePath)) &&
                    environment.IsAlive(process))
                .ToArray();

            if (ambiguousPackageWorkers.Length > 0)
            {
                throw new UpgradePreflightException(
                    "Package-owned NAPS2 worker exists but its WebAssistant parent identity cannot be proven.");
            }

            return;
        }

        var serviceProcess = service.Process;
        if (!string.Equals(
                NormalizeWindowsPath(serviceProcess.ImagePath),
                servicePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UpgradePreflightException(
                "SCM service process image does not match the configured WebAssistant executable.");
        }

        var capturedWorkers = new HashSet<ProcessIdentity>();

        CaptureOwnedWorkers(
            environment.SnapshotProcesses(),
            serviceProcess,
            allowedWorkerPaths,
            capturedWorkers);

        var serviceStopped = service.State == ServiceState.Stopped;
        var serviceProcessExited = !environment.IsAlive(serviceProcess);

        if (!serviceStopped)
        {
            await environment.RequestServiceStopAsync(cancellationToken);
        }

        var convergenceRemaining = policy.ServiceStopTimeout;
        while (!serviceStopped || !serviceProcessExited)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CaptureOwnedWorkers(
                environment.SnapshotProcesses(),
                serviceProcess,
                allowedWorkerPaths,
                capturedWorkers);

            if (convergenceRemaining <= TimeSpan.Zero)
            {
                throw new UpgradePreflightException(
                    serviceStopped
                        ? "WebAssistant service process did not exit within the preflight timeout."
                        : "WebAssistant service did not reach Stopped within the preflight timeout.");
            }

            var convergenceSlice = GetConvergenceSlice(convergenceRemaining);
            var serviceWait = serviceStopped
                ? Task.FromResult(true)
                : environment.WaitForServiceStoppedAsync(convergenceSlice, cancellationToken);
            var processWait = serviceProcessExited
                ? Task.FromResult(true)
                : environment.WaitForExitAsync(serviceProcess, convergenceSlice, cancellationToken);

            await Task.WhenAll(serviceWait, processWait);

            serviceStopped |= serviceWait.Result;
            serviceProcessExited |= processWait.Result;
            if (!serviceProcessExited)
            {
                serviceProcessExited = !environment.IsAlive(serviceProcess);
            }

            convergenceRemaining -= convergenceSlice;
        }

        var serviceExitTimeUtc = environment.GetExitTimeUtc(serviceProcess);
        if (serviceExitTimeUtc is not null)
        {
            if (serviceExitTimeUtc.Value < serviceProcess.StartTimeUtc)
            {
                throw new UpgradePreflightException(
                    "Exact WebAssistant service process exit time predates its creation time.");
            }

            CaptureOwnedWorkers(
                environment.SnapshotProcesses(),
                serviceProcess,
                allowedWorkerPaths,
                capturedWorkers,
                serviceExitTimeUtc.Value);
        }

        foreach (var worker in capturedWorkers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!environment.IsAlive(worker))
            {
                continue;
            }

            if (await environment.WaitForExitAsync(
                    worker,
                    policy.WorkerGraceTimeout,
                    cancellationToken))
            {
                continue;
            }

            environment.Terminate(worker);
            if (!await environment.WaitForExitAsync(
                    worker,
                    policy.PostTerminateTimeout,
                    cancellationToken))
            {
                throw new UpgradePreflightException(
                    $"Owned NAPS2 worker pid={worker.ProcessId} remained alive after termination.");
            }
        }
    }

    private TimeSpan GetConvergenceSlice(TimeSpan remaining)
    {
        var requested = policy.PollInterval > TimeSpan.Zero
            ? policy.PollInterval
            : DefaultConvergenceSlice;
        return requested < remaining ? requested : remaining;
    }

    private void CaptureOwnedWorkers(
        IReadOnlyList<ProcessIdentity> processes,
        ProcessIdentity serviceProcess,
        IReadOnlySet<string> allowedWorkerPaths,
        ISet<ProcessIdentity> capturedWorkers,
        DateTimeOffset? latestStartTimeUtc = null)
    {
        foreach (var process in processes)
        {
            if (process.ProcessId <= 0 ||
                process.ParentProcessId != serviceProcess.ProcessId ||
                process.StartTimeUtc < serviceProcess.StartTimeUtc ||
                (latestStartTimeUtc is not null && process.StartTimeUtc > latestStartTimeUtc.Value) ||
                !allowedWorkerPaths.Contains(NormalizeWindowsPath(process.ImagePath)) ||
                !environment.IsAlive(process))
            {
                continue;
            }

            capturedWorkers.Add(process);
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UpgradePreflightException("Required Windows executable path is empty.");
        }

        var normalized = path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length < 3 ||
            normalized.Contains("\\..\\", StringComparison.Ordinal) ||
            normalized.EndsWith("\\..", StringComparison.Ordinal) ||
            normalized.Contains("\\.\\", StringComparison.Ordinal) ||
            normalized.EndsWith("\\.", StringComparison.Ordinal))
        {
            throw new UpgradePreflightException(
                $"Unsafe Windows executable path: {path}");
        }

        return normalized;
    }

    private static string GetWindowsDirectory(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator <= 2 ? string.Empty : path[..separator].TrimEnd('\\');
    }
}
