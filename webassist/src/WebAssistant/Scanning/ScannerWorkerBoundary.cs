namespace WebAssistant.Scanning;

internal enum ScannerOperationKind
{
    Discovery,
    Capabilities,
    Acquisition
}

internal enum ScannerWorkerTerminationReason
{
    Deadline,
    ClientCancellation
}

internal interface IScannerWorkerLease
{
    int ProcessId { get; }

    Task TerminateAsync(
        ScannerWorkerTerminationReason reason,
        CancellationToken cancellationToken = default);
}

internal sealed class ScannerWorkerLeaseCapture
{
    private readonly TaskCompletionSource<IScannerWorkerLease> source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int published;

    internal bool IsPublished => Volatile.Read(ref published) == 1;

    internal void Publish(IScannerWorkerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (Interlocked.Exchange(ref published, 1) != 0)
        {
            throw new InvalidOperationException(
                "Для одной scanner operation нельзя публиковать более одного causal worker lease.");
        }

        source.TrySetResult(lease);
    }

    internal bool TryGet(out IScannerWorkerLease? lease)
    {
        if (source.Task.IsCompletedSuccessfully)
        {
            lease = source.Task.Result;
            return true;
        }

        lease = null;
        return false;
    }
}

internal sealed class ScannerOperationTimeoutException(
    ScannerOperationKind operation,
    ScannerBackend backend,
    TimeSpan timeout) : TimeoutException(
        $"Scanner operation '{operation}' for backend '{backend}' exceeded deadline {timeout}.")
{
    internal ScannerOperationKind Operation { get; } = operation;

    internal ScannerBackend Backend { get; } = backend;

    internal TimeSpan Timeout { get; } = timeout;
}

internal sealed class ScannerWorkerRecoveryException(
    ScannerOperationKind operation,
    ScannerBackend backend,
    int? workerProcessId,
    Exception? innerException = null) : Exception(
        workerProcessId.HasValue
            ? $"Не удалось подтвердить завершение scanner worker PID={workerProcessId.Value}."
            : "Не удалось установить causal worker lease для зависшей scanner operation.",
        innerException)
{
    internal ScannerOperationKind Operation { get; } = operation;

    internal ScannerBackend Backend { get; } = backend;

    internal int? WorkerProcessId { get; } = workerProcessId;
}

internal static class ScannerWorkerBoundary
{
    internal static async Task<T> ExecuteAsync<T>(
        ScannerOperationKind operation,
        ScannerBackend backend,
        TimeSpan timeout,
        CancellationToken requestCancellation,
        ScannerWorkerLeaseCapture leaseCapture,
        Func<Task<T>> operationFactory)
    {
        ArgumentNullException.ThrowIfNull(leaseCapture);
        ArgumentNullException.ThrowIfNull(operationFactory);

        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Scanner operation deadline должен быть конечным положительным интервалом.");
        }

        requestCancellation.ThrowIfCancellationRequested();

        using var deadlineCancellation = new CancellationTokenSource(timeout);
        var deadlineTask = Task.Delay(
            Timeout.InfiniteTimeSpan,
            deadlineCancellation.Token);

        // Run the factory itself behind the boundary: NAPS2 worker acquisition and
        // initialization contain synchronous work before the remote async Task is returned.
        var operationTask = Task.Run(operationFactory);

        if (operationTask.IsCompleted)
        {
            return await operationTask.ConfigureAwait(false);
        }
        var requestCancellationTask = requestCancellation.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, requestCancellation)
            : Task.Delay(Timeout.InfiniteTimeSpan);

        var completed = await Task.WhenAny(
                operationTask,
                deadlineTask,
                requestCancellationTask)
            .ConfigureAwait(false);

        if (operationTask.IsCompleted || ReferenceEquals(completed, operationTask))
        {
            return await operationTask.ConfigureAwait(false);
        }

        var reason = requestCancellation.IsCancellationRequested
            ? ScannerWorkerTerminationReason.ClientCancellation
            : ScannerWorkerTerminationReason.Deadline;

        // A backend result/error that won the race must remain authoritative.
        if (operationTask.IsCompleted)
        {
            return await operationTask.ConfigureAwait(false);
        }

        if (!leaseCapture.TryGet(out var lease) || lease is null)
        {
            ObserveLateCompletion(operationTask);
            throw new ScannerWorkerRecoveryException(
                operation,
                backend,
                workerProcessId: null);
        }

        try
        {
            await lease.TerminateAsync(reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ScannerWorkerRecoveryException(
                operation,
                backend,
                lease.ProcessId,
                exception);
        }

        ObserveLateCompletion(operationTask);

        if (reason == ScannerWorkerTerminationReason.ClientCancellation)
        {
            throw new OperationCanceledException(requestCancellation);
        }

        throw new ScannerOperationTimeoutException(
            operation,
            backend,
            timeout);
    }

    private static void ObserveLateCompletion<T>(Task<T> task)
    {
        _ = task.ContinueWith(
            static completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
