namespace WebAssistant.UpgradePreflight;

internal enum ServiceState
{
    Stopped,
    Running,
    StartPending,
    StopPending
}

internal sealed record ServiceSnapshot(
    string ExecutablePath,
    ServiceState State,
    ProcessIdentity? Process);

internal interface IUpgradeEnvironment
{
    ServiceSnapshot? TryGetWebAssistantService();

    IReadOnlyList<ProcessIdentity> SnapshotProcesses();

    Task RequestServiceStopAsync(CancellationToken cancellationToken);

    Task<bool> WaitForServiceStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    bool IsAlive(ProcessIdentity process);

    Task<bool> WaitForExitAsync(
        ProcessIdentity process,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    void Terminate(ProcessIdentity process);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
