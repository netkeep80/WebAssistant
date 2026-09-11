using WebAssistant.UpgradePreflight;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsUpgradePreflightRaceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
    private const string ServicePath = @"C:\Program Files\WebAssistant\WebAssistant.exe";
    private const string WorkerPath = @"C:\Program Files\WebAssistant\NAPS2.Worker.exe";

    [Fact]
    public async Task WorkerCreatedImmediatelyBeforeParentExit_IsCapturedFromFinalSnapshot()
    {
        var service = new ProcessIdentity(100, 4, ServicePath, T0);
        var lateWorker = new ProcessIdentity(200, 100, WorkerPath, T0.AddSeconds(2));
        var environment = new ParentExitRaceEnvironment(
            service,
            serviceExitTimeUtc: T0.AddSeconds(3),
            snapshots: [[], [lateWorker]]);
        environment.SetAlive(lateWorker, true);
        environment.SetWaitSequence(lateWorker, false, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.True(environment.SnapshotCalls >= 2);
        Assert.Equal([lateWorker], environment.Terminated);
    }

    [Fact]
    public async Task PidReuseAfterExactParentExit_IsExcludedFromFinalSnapshot()
    {
        var service = new ProcessIdentity(100, 4, ServicePath, T0);
        var reusedParentWorker = new ProcessIdentity(201, 100, WorkerPath, T0.AddSeconds(4));
        var environment = new ParentExitRaceEnvironment(
            service,
            serviceExitTimeUtc: T0.AddSeconds(3),
            snapshots: [[], [reusedParentWorker]]);
        environment.SetAlive(reusedParentWorker, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.True(environment.SnapshotCalls >= 2);
        Assert.Empty(environment.Terminated);
        Assert.DoesNotContain(reusedParentWorker, environment.WaitedForExit);
    }

    [Fact]
    public async Task ServiceReportedStoppedButExactParentNeverExits_FailsClosedWithinBoundedConvergence()
    {
        var service = new ProcessIdentity(100, 4, ServicePath, T0);
        var environment = new NeverExitingParentEnvironment(service, maxAliveChecks: 6);
        var preflight = new UpgradePreflightOrchestrator(
            environment,
            new UpgradePreflightPolicy(
                ServiceStopTimeout: TimeSpan.FromSeconds(3),
                WorkerGraceTimeout: TimeSpan.FromSeconds(3),
                PostTerminateTimeout: TimeSpan.FromSeconds(2),
                PollInterval: TimeSpan.FromSeconds(1)));

        var error = await Assert.ThrowsAsync<UpgradePreflightException>(
            () => preflight.RunAsync(CancellationToken.None));

        Assert.Contains("service process", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(environment.ServiceAliveChecks, 1, 6);
    }

    private static UpgradePreflightOrchestrator CreatePreflight(ParentExitRaceEnvironment environment) =>
        new(
            environment,
            new UpgradePreflightPolicy(
                ServiceStopTimeout: TimeSpan.FromSeconds(15),
                WorkerGraceTimeout: TimeSpan.FromSeconds(3),
                PostTerminateTimeout: TimeSpan.FromSeconds(2),
                PollInterval: TimeSpan.Zero));

    private sealed class ParentExitRaceEnvironment : IUpgradeEnvironment
    {
        private readonly Queue<IReadOnlyList<ProcessIdentity>> snapshots;
        private readonly Dictionary<ProcessIdentity, bool> alive = new();
        private readonly Dictionary<ProcessIdentity, Queue<bool>> waitSequences = new();
        private readonly ProcessIdentity service;
        private readonly DateTimeOffset serviceExitTimeUtc;

        internal ParentExitRaceEnvironment(
            ProcessIdentity service,
            DateTimeOffset serviceExitTimeUtc,
            IEnumerable<IReadOnlyList<ProcessIdentity>> snapshots)
        {
            this.service = service;
            this.serviceExitTimeUtc = serviceExitTimeUtc;
            this.snapshots = new Queue<IReadOnlyList<ProcessIdentity>>(snapshots);
        }

        internal int SnapshotCalls { get; private set; }
        internal List<ProcessIdentity> Terminated { get; } = [];
        internal List<ProcessIdentity> WaitedForExit { get; } = [];

        internal void SetAlive(ProcessIdentity process, bool value) => alive[process] = value;

        internal void SetWaitSequence(ProcessIdentity process, params bool[] values) =>
            waitSequences[process] = new Queue<bool>(values);

        public ServiceSnapshot? TryGetWebAssistantService() =>
            new(ServicePath, ServiceState.Running, service);

        public IReadOnlyList<ProcessIdentity> SnapshotProcesses()
        {
            SnapshotCalls++;
            if (snapshots.Count == 0)
            {
                return [];
            }

            if (snapshots.Count == 1)
            {
                return snapshots.Peek();
            }

            return snapshots.Dequeue();
        }

        public Task RequestServiceStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> WaitForServiceStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public bool IsAlive(ProcessIdentity process) =>
            process == service ? false : alive.GetValueOrDefault(process);

        public DateTimeOffset? GetExitTimeUtc(ProcessIdentity process) =>
            process == service ? serviceExitTimeUtc : null;

        public Task<bool> WaitForExitAsync(
            ProcessIdentity process,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitedForExit.Add(process);
            if (!waitSequences.TryGetValue(process, out var sequence) || sequence.Count == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(sequence.Count == 1 ? sequence.Peek() : sequence.Dequeue());
        }

        public void Terminate(ProcessIdentity process) => Terminated.Add(process);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NeverExitingParentEnvironment : IUpgradeEnvironment
    {
        private readonly ProcessIdentity service;
        private readonly int maxAliveChecks;

        internal NeverExitingParentEnvironment(ProcessIdentity service, int maxAliveChecks)
        {
            this.service = service;
            this.maxAliveChecks = maxAliveChecks;
        }

        internal int ServiceAliveChecks { get; private set; }

        public ServiceSnapshot? TryGetWebAssistantService() =>
            new(ServicePath, ServiceState.Running, service);

        public IReadOnlyList<ProcessIdentity> SnapshotProcesses() => [];

        public Task RequestServiceStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> WaitForServiceStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public bool IsAlive(ProcessIdentity process)
        {
            if (process != service)
            {
                return false;
            }

            ServiceAliveChecks++;
            if (ServiceAliveChecks > maxAliveChecks)
            {
                throw new InvalidOperationException(
                    "Test guard: unbounded exact service-process convergence loop detected.");
            }

            return true;
        }

        public DateTimeOffset? GetExitTimeUtc(ProcessIdentity process) => null;

        public Task<bool> WaitForExitAsync(
            ProcessIdentity process,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public void Terminate(ProcessIdentity process) =>
            throw new InvalidOperationException("The preflight must never terminate the WebAssistant service process directly.");

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
