using WebAssistant.UpgradePreflight;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsUpgradePreflightTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
    private const string InstallRoot = @"C:\Program Files\WebAssistant";
    private const string ServicePath = InstallRoot + @"\WebAssistant.exe";
    private const string WorkerPath = InstallRoot + @"\NAPS2.Worker.exe";
    private const string LibWorkerPath = InstallRoot + @"\lib\NAPS2.Worker.exe";

    [Fact]
    public async Task ServiceAbsent_IsIdempotentNoOp()
    {
        var environment = new FakeUpgradeEnvironment();
        var preflight = CreatePreflight(environment);

        await preflight.RunAsync(CancellationToken.None);
        await preflight.RunAsync(CancellationToken.None);

        Assert.Equal(0, environment.StopRequests);
        Assert.Empty(environment.Terminated);
        Assert.Equal(2, environment.ServiceQueries);
    }

    [Fact]
    public async Task RunningCanonicalService_RequestsOrderlyStop()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([]);
        environment.SetAliveSequence(serviceProcess, false);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Equal(1, environment.StopRequests);
        Assert.Equal(1, environment.ServiceStopWaits);
        Assert.Empty(environment.Terminated);
    }

    [Fact]
    public async Task ExactPathParentAndLifetime_CapturesOwnedWorker()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var worker = Process(200, 100, WorkerPath, T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([worker]);
        environment.SetAliveSequence(serviceProcess, false);
        environment.SetAliveSequence(worker, true);
        environment.SetWaitForExitSequence(worker, false, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Equal([worker], environment.Terminated);
    }

    [Fact]
    public async Task LibWorkerPath_IsAlsoOwnedWhenParentAndLifetimeMatch()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var worker = Process(201, 100, LibWorkerPath, T0.AddMilliseconds(10));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([worker]);
        environment.SetAliveSequence(serviceProcess, false);
        environment.SetAliveSequence(worker, true);
        environment.SetWaitForExitSequence(worker, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
        Assert.Contains(worker, environment.WaitedForExit);
    }

    [Fact]
    public async Task SameFilenameOutsideInstallRoot_IsUntouched()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var unrelated = Process(200, 100, @"C:\OtherApp\NAPS2.Worker.exe", T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([unrelated]);
        environment.SetAliveSequence(serviceProcess, false);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
        Assert.DoesNotContain(unrelated, environment.WaitedForExit);
    }

    [Fact]
    public async Task ExpectedWorkerPathWithWrongParent_IsUntouched()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var unrelated = Process(200, 999, WorkerPath, T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([unrelated]);
        environment.SetAliveSequence(serviceProcess, false);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
        Assert.DoesNotContain(unrelated, environment.WaitedForExit);
    }

    [Fact]
    public async Task WorkerCreatedBeforeParentExit_IsCapturedAcrossSnapshots()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var lateWorker = Process(202, 100, WorkerPath, T0.AddSeconds(2));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([]);
        environment.EnqueueSnapshot([lateWorker]);
        environment.SetAliveSequence(serviceProcess, true, false);
        environment.SetAliveSequence(lateWorker, true);
        environment.SetWaitForExitSequence(lateWorker, false, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Equal([lateWorker], environment.Terminated);
        Assert.True(environment.SnapshotCalls >= 2);
    }

    [Fact]
    public async Task PidReuseAfterRecordedParentExit_IsNeverAddedToOwnedSet()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var reusedParentWorker = Process(203, 100, WorkerPath, T0.AddMinutes(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([]);
        environment.EnqueueSnapshot([reusedParentWorker]);
        environment.SetAliveSequence(serviceProcess, false);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
        Assert.DoesNotContain(reusedParentWorker, environment.WaitedForExit);
        Assert.Equal(1, environment.SnapshotCalls);
    }

    [Fact]
    public async Task WorkerPredatingServiceInstance_IsNotOwned()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var oldWorker = Process(204, 100, WorkerPath, T0.AddSeconds(-1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([oldWorker]);
        environment.SetAliveSequence(serviceProcess, false);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
        Assert.DoesNotContain(oldWorker, environment.WaitedForExit);
    }

    [Fact]
    public async Task AlreadyStoppedServiceWithUnprovablePackageWorker_FailsClosedWithoutKilling()
    {
        var orphan = Process(300, 100, WorkerPath, T0.AddSeconds(5));
        var environment = new FakeUpgradeEnvironment
        {
            Service = new ServiceSnapshot(ServicePath, ServiceState.Stopped, null)
        };
        environment.EnqueueSnapshot([orphan]);
        environment.SetAliveSequence(orphan, true);

        await Assert.ThrowsAsync<UpgradePreflightException>(
            () => CreatePreflight(environment).RunAsync(CancellationToken.None));

        Assert.Equal(0, environment.StopRequests);
        Assert.Empty(environment.Terminated);
    }

    [Fact]
    public async Task AlreadyStoppedServiceWithoutPackageWorker_IsCleanSuccess()
    {
        var environment = new FakeUpgradeEnvironment
        {
            Service = new ServiceSnapshot(ServicePath, ServiceState.Stopped, null)
        };
        environment.EnqueueSnapshot([
            Process(301, 100, @"C:\OtherApp\NAPS2.Worker.exe", T0.AddSeconds(5))
        ]);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Equal(0, environment.StopRequests);
        Assert.Empty(environment.Terminated);
    }

    [Fact]
    public async Task ServiceStopTimeout_FailsClosed()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var environment = RunningService(serviceProcess);
        environment.ServiceStopped = false;
        environment.EnqueueSnapshot([]);

        await Assert.ThrowsAsync<UpgradePreflightException>(
            () => CreatePreflight(environment).RunAsync(CancellationToken.None));

        Assert.Equal(1, environment.StopRequests);
        Assert.Empty(environment.Terminated);
    }

    [Fact]
    public async Task OwnedWorkerExitsDuringGrace_IsNotTerminated()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var worker = Process(200, 100, WorkerPath, T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([worker]);
        environment.SetAliveSequence(serviceProcess, false);
        environment.SetAliveSequence(worker, true);
        environment.SetWaitForExitSequence(worker, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Empty(environment.Terminated);
    }

    [Fact]
    public async Task OwnedWorkerSurvivesGrace_TerminatesOnlyExactCapturedIdentity()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var owned = Process(200, 100, WorkerPath, T0.AddSeconds(1));
        var unrelated = Process(201, 999, WorkerPath, T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([owned, unrelated]);
        environment.SetAliveSequence(serviceProcess, false);
        environment.SetAliveSequence(owned, true);
        environment.SetWaitForExitSequence(owned, false, true);

        await CreatePreflight(environment).RunAsync(CancellationToken.None);

        Assert.Equal([owned], environment.Terminated);
        Assert.DoesNotContain(unrelated, environment.Terminated);
    }

    [Fact]
    public async Task WorkerStillAliveAfterTermination_FailsClosed()
    {
        var serviceProcess = Process(100, 4, ServicePath, T0);
        var worker = Process(200, 100, WorkerPath, T0.AddSeconds(1));
        var environment = RunningService(serviceProcess);
        environment.EnqueueSnapshot([worker]);
        environment.SetAliveSequence(serviceProcess, false);
        environment.SetAliveSequence(worker, true);
        environment.SetWaitForExitSequence(worker, false, false);

        await Assert.ThrowsAsync<UpgradePreflightException>(
            () => CreatePreflight(environment).RunAsync(CancellationToken.None));

        Assert.Equal([worker], environment.Terminated);
    }

    private static UpgradePreflightOrchestrator CreatePreflight(FakeUpgradeEnvironment environment) =>
        new(
            environment,
            new UpgradePreflightPolicy(
                ServiceStopTimeout: TimeSpan.FromSeconds(15),
                WorkerGraceTimeout: TimeSpan.FromSeconds(3),
                PostTerminateTimeout: TimeSpan.FromSeconds(2),
                PollInterval: TimeSpan.Zero));

    private static FakeUpgradeEnvironment RunningService(ProcessIdentity serviceProcess) =>
        new()
        {
            Service = new ServiceSnapshot(ServicePath, ServiceState.Running, serviceProcess)
        };

    private static ProcessIdentity Process(
        int pid,
        int parentPid,
        string path,
        DateTimeOffset startTimeUtc) =>
        new(pid, parentPid, path, startTimeUtc);

    private sealed class FakeUpgradeEnvironment : IUpgradeEnvironment
    {
        private readonly Queue<IReadOnlyList<ProcessIdentity>> snapshots = new();
        private readonly Dictionary<ProcessIdentity, Queue<bool>> aliveSequences = new();
        private readonly Dictionary<ProcessIdentity, Queue<bool>> waitSequences = new();

        internal ServiceSnapshot? Service { get; init; }
        internal bool ServiceStopped { get; set; } = true;
        internal int ServiceQueries { get; private set; }
        internal int StopRequests { get; private set; }
        internal int ServiceStopWaits { get; private set; }
        internal int SnapshotCalls { get; private set; }
        internal List<ProcessIdentity> Terminated { get; } = [];
        internal List<ProcessIdentity> WaitedForExit { get; } = [];

        internal void EnqueueSnapshot(IReadOnlyList<ProcessIdentity> processes) =>
            snapshots.Enqueue(processes);

        internal void SetAliveSequence(ProcessIdentity process, params bool[] values) =>
            aliveSequences[process] = new Queue<bool>(values);

        internal void SetWaitForExitSequence(ProcessIdentity process, params bool[] values) =>
            waitSequences[process] = new Queue<bool>(values);

        public ServiceSnapshot? TryGetWebAssistantService()
        {
            ServiceQueries++;
            return Service;
        }

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

        public Task RequestServiceStopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopRequests++;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForServiceStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceStopWaits++;
            return Task.FromResult(ServiceStopped);
        }

        public bool IsAlive(ProcessIdentity process) =>
            Next(aliveSequences, process, false);

        public Task<bool> WaitForExitAsync(
            ProcessIdentity process,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitedForExit.Add(process);
            return Task.FromResult(Next(waitSequences, process, true));
        }

        public void Terminate(ProcessIdentity process) =>
            Terminated.Add(process);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private static bool Next(
            IDictionary<ProcessIdentity, Queue<bool>> sequences,
            ProcessIdentity process,
            bool fallback)
        {
            if (!sequences.TryGetValue(process, out var sequence) || sequence.Count == 0)
            {
                return fallback;
            }

            if (sequence.Count == 1)
            {
                return sequence.Peek();
            }

            return sequence.Dequeue();
        }
    }
}
