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

        if (service.State != ServiceState.Stopped)
        {
            await environment.RequestServiceStopAsync(cancellationToken);
            if (!await environment.WaitForServiceStoppedAsync(
                    policy.ServiceStopTimeout,
                    cancellationToken))
            {
                throw new UpgradePreflightException(
                    "WebAssistant service did not reach Stopped within the preflight timeout.");
            }
        }

        while (environment.IsAlive(serviceProcess))
        {
            CaptureOwnedWorkers(
                environment.SnapshotProcesses(),
                serviceProcess,
                allowedWorkerPaths,
                capturedWorkers);

            await environment.DelayAsync(policy.PollInterval, cancellationToken);
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

    private void CaptureOwnedWorkers(
        IReadOnlyList<ProcessIdentity> processes,
        ProcessIdentity serviceProcess,
        IReadOnlySet<string> allowedWorkerPaths,
        ISet<ProcessIdentity> capturedWorkers)
    {
        foreach (var process in processes)
        {
            if (process.ProcessId <= 0 ||
                process.ParentProcessId != serviceProcess.ProcessId ||
                process.StartTimeUtc < serviceProcess.StartTimeUtc ||
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
