using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerWorkerBoundaryTests
{
    [Fact]
    public async Task Deadline_HangingOperationTerminatesExactLeaseBeforeTimeoutSurfaces()
    {
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new FakeWorkerLease(41001);
        var capture = new ScannerWorkerLeaseCapture();
        capture.Publish(lease);

        var exception = await Assert.ThrowsAsync<ScannerOperationTimeoutException>(
            () => ScannerWorkerBoundary.ExecuteAsync(
                ScannerOperationKind.Capabilities,
                ScannerBackend.Twain,
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None,
                capture,
                () => backend.Task));

        Assert.Equal(ScannerOperationKind.Capabilities, exception.Operation);
        Assert.Equal(ScannerBackend.Twain, exception.Backend);
        Assert.Equal(41001, lease.ProcessId);
        Assert.Equal(1, lease.TerminationCalls);
        Assert.Equal(
            ScannerWorkerTerminationReason.Deadline,
            lease.LastTerminationReason);
        Assert.True(lease.TerminationCompleted);
        Assert.False(backend.Task.IsCompleted);
    }

    [Fact]
    public async Task Deadline_IncludesSynchronousOperationStartupAfterLeasePublication()
    {
        var lease = new FakeWorkerLease(41021);
        var capture = new ScannerWorkerLeaseCapture();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ScannerOperationTimeoutException>(
            () => ScannerWorkerBoundary.ExecuteAsync(
                ScannerOperationKind.Capabilities,
                ScannerBackend.Twain,
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None,
                capture,
                () =>
                {
                    capture.Publish(lease);
                    Thread.Sleep(250);
                    return Task.FromResult(91);
                }));

        stopwatch.Stop();

        Assert.Equal(ScannerOperationKind.Capabilities, exception.Operation);
        Assert.Equal(1, lease.TerminationCalls);
        Assert.Equal(
            ScannerWorkerTerminationReason.Deadline,
            lease.LastTerminationReason);
        Assert.True(lease.TerminationCompleted);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Deadline started too late: elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
    }

    [Fact]
    public async Task ClientCancellation_HangingOperationTerminatesExactLeaseBeforeCancellationSurfaces()
    {
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new FakeWorkerLease(41002);
        var capture = new ScannerWorkerLeaseCapture();
        capture.Publish(lease);
        using var cancellation = new CancellationTokenSource();

        var operation = ScannerWorkerBoundary.ExecuteAsync(
            ScannerOperationKind.Capabilities,
            ScannerBackend.Twain,
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            capture,
            () => backend.Task);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation);

        Assert.Equal(1, lease.TerminationCalls);
        Assert.Equal(
            ScannerWorkerTerminationReason.ClientCancellation,
            lease.LastTerminationReason);
        Assert.True(lease.TerminationCompleted);
        Assert.False(backend.Task.IsCompleted);
    }

    [Fact]
    public async Task BackendCompletionBeforeDeadlineDoesNotTerminateWorker()
    {
        var lease = new FakeWorkerLease(41003);
        var capture = new ScannerWorkerLeaseCapture();
        capture.Publish(lease);

        var value = await ScannerWorkerBoundary.ExecuteAsync(
            ScannerOperationKind.Discovery,
            ScannerBackend.Twain,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            capture,
            () => Task.FromResult(73));

        Assert.Equal(73, value);
        Assert.Equal(0, lease.TerminationCalls);
    }

    [Fact]
    public async Task BackendFailureBeforeDeadlineIsPreserved()
    {
        var lease = new FakeWorkerLease(41004);
        var capture = new ScannerWorkerLeaseCapture();
        capture.Publish(lease);
        var expected = new MarkerException();

        var actual = await Assert.ThrowsAsync<MarkerException>(
            () => ScannerWorkerBoundary.ExecuteAsync<int>(
                ScannerOperationKind.Capabilities,
                ScannerBackend.Twain,
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                capture,
                () => Task.FromException<int>(expected)));

        Assert.Same(expected, actual);
        Assert.Equal(0, lease.TerminationCalls);
    }

    [Fact]
    public async Task RepeatedHangsRetireDistinctWorkersWithoutAccumulation()
    {
        var leases = new[]
        {
            new FakeWorkerLease(41011),
            new FakeWorkerLease(41012),
            new FakeWorkerLease(41013)
        };

        foreach (var lease in leases)
        {
            var backend = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var capture = new ScannerWorkerLeaseCapture();
            capture.Publish(lease);

            await Assert.ThrowsAsync<ScannerOperationTimeoutException>(
                () => ScannerWorkerBoundary.ExecuteAsync(
                    ScannerOperationKind.Capabilities,
                    ScannerBackend.Twain,
                    TimeSpan.FromMilliseconds(20),
                    CancellationToken.None,
                    capture,
                    () => backend.Task));
        }

        Assert.All(leases, lease =>
        {
            Assert.Equal(1, lease.TerminationCalls);
            Assert.True(lease.TerminationCompleted);
        });
        Assert.Equal(
            leases.Length,
            leases.Select(lease => lease.ProcessId).Distinct().Count());
    }

    [Fact]
    public async Task TerminationFailureIsRecoveryFailureNotSuccessfulTimeout()
    {
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new FakeWorkerLease(
            41020,
            terminationError: new TimeoutException("worker-still-alive"));
        var capture = new ScannerWorkerLeaseCapture();
        capture.Publish(lease);

        var exception = await Assert.ThrowsAsync<ScannerWorkerRecoveryException>(
            () => ScannerWorkerBoundary.ExecuteAsync(
                ScannerOperationKind.Capabilities,
                ScannerBackend.Twain,
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None,
                capture,
                () => backend.Task));

        Assert.Equal(41020, exception.WorkerProcessId);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, lease.TerminationCalls);
        Assert.False(lease.TerminationCompleted);
    }

    [Fact]
    public async Task MissingLeaseOnDeadlineFailsClosedAsRecoveryFailure()
    {
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new ScannerWorkerLeaseCapture();

        var exception = await Assert.ThrowsAsync<ScannerWorkerRecoveryException>(
            () => ScannerWorkerBoundary.ExecuteAsync(
                ScannerOperationKind.Capabilities,
                ScannerBackend.Twain,
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None,
                capture,
                () => backend.Task));

        Assert.Null(exception.WorkerProcessId);
    }

    private sealed class FakeWorkerLease(
        int processId,
        Exception? terminationError = null) : IScannerWorkerLease
    {
        private int terminationCalls;

        internal int TerminationCalls => Volatile.Read(ref terminationCalls);

        internal ScannerWorkerTerminationReason? LastTerminationReason { get; private set; }

        internal bool TerminationCompleted { get; private set; }

        public int ProcessId { get; } = processId;

        public async Task TerminateAsync(
            ScannerWorkerTerminationReason reason,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref terminationCalls);
            LastTerminationReason = reason;

            await Task.Delay(5, cancellationToken);

            if (terminationError is not null)
            {
                throw terminationError;
            }

            TerminationCompleted = true;
        }
    }

    private sealed class MarkerException : Exception;
}
