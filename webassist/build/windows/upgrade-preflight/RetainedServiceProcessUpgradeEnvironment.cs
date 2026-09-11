using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WebAssistant.UpgradePreflight;

internal sealed class RetainedServiceProcessUpgradeEnvironment : IUpgradeEnvironment, IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    private readonly WindowsUpgradeEnvironment inner = new();
    private SafeProcessHandle? retainedServiceHandle;
    private ProcessIdentity? retainedServiceIdentity;
    private bool disposed;

    public ServiceSnapshot? TryGetWebAssistantService()
    {
        ThrowIfDisposed();

        var snapshot = inner.TryGetWebAssistantService();
        if (snapshot?.Process is null)
        {
            ReleaseRetainedServiceProcess();
            return snapshot;
        }

        RetainExactServiceProcess(snapshot.Process);
        return snapshot;
    }

    public IReadOnlyList<ProcessIdentity> SnapshotProcesses()
    {
        ThrowIfDisposed();
        return inner.SnapshotProcesses();
    }

    public Task RequestServiceStopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return inner.RequestServiceStopAsync(cancellationToken);
    }

    public Task<bool> WaitForServiceStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return inner.WaitForServiceStoppedAsync(timeout, cancellationToken);
    }

    public bool IsAlive(ProcessIdentity process)
    {
        ThrowIfDisposed();
        return inner.IsAlive(process);
    }

    public DateTimeOffset? GetExitTimeUtc(ProcessIdentity process)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(process);

        if (retainedServiceHandle is null ||
            retainedServiceIdentity is null ||
            retainedServiceIdentity != process)
        {
            throw new UpgradePreflightException(
                "Exact WebAssistant service process handle was not retained before shutdown.");
        }

        if (!GetProcessTimes(
                retainedServiceHandle,
                out var creation,
                out var exit,
                out _,
                out _))
        {
            throw Win32Failure($"GetProcessTimes(pid={process.ProcessId})");
        }

        var retainedStartTimeUtc = DateTimeOffset.FromFileTime(creation.ToLong());
        if (retainedStartTimeUtc != process.StartTimeUtc)
        {
            throw new UpgradePreflightException(
                "Retained WebAssistant service process identity changed unexpectedly.");
        }

        var exitFileTime = exit.ToLong();
        if (exitFileTime == 0)
        {
            throw new UpgradePreflightException(
                "WebAssistant service process was reported gone before an exact exit time was available.");
        }

        return DateTimeOffset.FromFileTime(exitFileTime);
    }

    public Task<bool> WaitForExitAsync(
        ProcessIdentity process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return inner.WaitForExitAsync(process, timeout, cancellationToken);
    }

    public void Terminate(ProcessIdentity process)
    {
        ThrowIfDisposed();
        inner.Terminate(process);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return inner.DelayAsync(delay, cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ReleaseRetainedServiceProcess();
    }

    private void RetainExactServiceProcess(ProcessIdentity expected)
    {
        if (retainedServiceIdentity == expected && retainedServiceHandle is not null)
        {
            return;
        }

        ReleaseRetainedServiceProcess();

        var handle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            checked((uint)expected.ProcessId));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw Win32Failure($"OpenProcess(pid={expected.ProcessId})", error);
        }

        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                throw Win32Failure($"GetProcessTimes(pid={expected.ProcessId})");
            }

            var actualStartTimeUtc = DateTimeOffset.FromFileTime(creation.ToLong());
            var actualPath = QueryProcessImagePath(handle, expected.ProcessId);
            if (actualStartTimeUtc != expected.StartTimeUtc ||
                !string.Equals(
                    actualPath,
                    Path.GetFullPath(expected.ImagePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UpgradePreflightException(
                    "SCM WebAssistant process identity changed before the preflight retained its handle.");
            }

            retainedServiceHandle = handle;
            retainedServiceIdentity = expected;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string QueryProcessImagePath(SafeProcessHandle handle, int processId)
    {
        var capacity = 32768;
        var path = new StringBuilder(capacity);
        var size = capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size))
        {
            throw Win32Failure($"QueryFullProcessImageName(pid={processId})");
        }

        return Path.GetFullPath(path.ToString());
    }

    private void ReleaseRetainedServiceProcess()
    {
        retainedServiceHandle?.Dispose();
        retainedServiceHandle = null;
        retainedServiceIdentity = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static UpgradePreflightException Win32Failure(
        string operation,
        int? errorOverride = null)
    {
        var error = errorOverride ?? Marshal.GetLastWin32Error();
        return new UpgradePreflightException(
            $"{operation} failed with Win32 error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint lowDateTime;
        private readonly uint highDateTime;

        internal long ToLong() =>
            ((long)highDateTime << 32) | lowDateTime;
    }

    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeProcessHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
