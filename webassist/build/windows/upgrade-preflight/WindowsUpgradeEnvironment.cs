using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WebAssistant.UpgradePreflight;

internal sealed class WindowsUpgradeEnvironment : IUpgradeEnvironment
{
    private const string ServiceName = "WebAssistant";
    private const string WorkerExecutableName = "NAPS2.Worker.exe";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;

    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;

    private static readonly TimeSpan ServicePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProcessWaitSlice = TimeSpan.FromMilliseconds(100);

    public ServiceSnapshot? TryGetWebAssistantService()
    {
        EnsureWindows();

        using var service = OpenWebAssistantService(
            ServiceQueryConfig | ServiceQueryStatus,
            allowMissing: true);
        if (service is null)
        {
            return null;
        }

        var configuredExecutable = QueryConfiguredExecutablePath(service);
        var status = QueryStatus(service);
        var state = MapServiceState(status.CurrentState);

        ProcessIdentity? process = null;
        if (status.ProcessId != 0)
        {
            var parentPid = FindParentProcessId(status.ProcessId);
            process = TryReadServiceProcessIdentity(
                checked((int)status.ProcessId),
                parentPid,
                configuredExecutable,
                allowInaccessible: false);
        }

        return new ServiceSnapshot(configuredExecutable, state, process);
    }

    public IReadOnlyList<ProcessIdentity> SnapshotProcesses()
    {
        EnsureWindows();

        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw Win32Failure("CreateToolhelp32Snapshot");
        }

        var processes = new List<ProcessIdentity>();
        var entry = PROCESSENTRY32.Create();
        if (!Process32First(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 18) // ERROR_NO_MORE_FILES
            {
                return processes;
            }

            throw Win32Failure("Process32First", error);
        }

        do
        {
            if (entry.ProcessId is > 0 and <= int.MaxValue &&
                IsWorkerSnapshotCandidate(entry.ExeFile))
            {
                var identity = TryReadProcessIdentity(
                    checked((int)entry.ProcessId),
                    checked((int)entry.ParentProcessId),
                    allowInaccessible: true);
                if (identity is not null)
                {
                    processes.Add(identity);
                }
            }

            entry = PROCESSENTRY32.Create();
        }
        while (Process32Next(snapshot, ref entry));

        return processes;
    }

    public Task RequestServiceStopAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        using var service = OpenWebAssistantService(ServiceQueryStatus | ServiceStop, allowMissing: false)!;
        var status = QueryStatus(service);
        if (status.CurrentState == NativeServiceStopped ||
            status.CurrentState == NativeServiceStopPending)
        {
            return Task.CompletedTask;
        }

        if (!ControlService(service, ServiceControlStop, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
            {
                throw Win32Failure("ControlService(SERVICE_CONTROL_STOP)", error);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<bool> WaitForServiceStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var service = OpenWebAssistantService(ServiceQueryStatus, allowMissing: true);
            if (service is null || QueryStatus(service).CurrentState == NativeServiceStopped)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(ServicePollInterval, cancellationToken);
        }
    }

    public bool IsAlive(ProcessIdentity process)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(process);

        using var handle = OpenExactProcess(
            process,
            ProcessQueryLimitedInformation | Synchronize,
            allowGone: true);
        if (handle is null)
        {
            return false;
        }

        var wait = WaitForSingleObject(handle, 0);
        return wait switch
        {
            WaitObject0 => false,
            WaitTimeout => true,
            WaitFailed => throw Win32Failure("WaitForSingleObject"),
            _ => throw new UpgradePreflightException(
                $"Unexpected wait result 0x{wait:X8} for pid={process.ProcessId}.")
        };
    }

    public async Task<bool> WaitForExitAsync(
        ProcessIdentity process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(process);

        using var handle = OpenExactProcess(
            process,
            ProcessQueryLimitedInformation | Synchronize,
            allowGone: true);
        if (handle is null)
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return WaitForSingleObject(handle, 0) == WaitObject0;
            }

            var slice = remaining < ProcessWaitSlice ? remaining : ProcessWaitSlice;
            var milliseconds = checked((uint)Math.Max(1, Math.Ceiling(slice.TotalMilliseconds)));
            var wait = WaitForSingleObject(handle, milliseconds);
            switch (wait)
            {
                case WaitObject0:
                    return true;
                case WaitTimeout:
                    await Task.Yield();
                    break;
                case WaitFailed:
                    throw Win32Failure("WaitForSingleObject");
                default:
                    throw new UpgradePreflightException(
                        $"Unexpected wait result 0x{wait:X8} for pid={process.ProcessId}.");
            }
        }
    }

    public void Terminate(ProcessIdentity process)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(process);

        using var handle = OpenExactProcess(
            process,
            ProcessQueryLimitedInformation | Synchronize | ProcessTerminate,
            allowGone: true);
        if (handle is null)
        {
            return;
        }

        if (!TerminateProcess(handle, 1))
        {
            throw Win32Failure($"TerminateProcess(pid={process.ProcessId})");
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);

    internal static bool IsWorkerSnapshotCandidate(string executableName) =>
        string.Equals(
            executableName,
            WorkerExecutableName,
            StringComparison.OrdinalIgnoreCase);

    internal static ProcessIdentity CreateServiceProcessIdentity(
        int processId,
        int parentProcessId,
        string configuredExecutablePath,
        DateTimeOffset startTimeUtc) =>
        new(
            processId,
            parentProcessId,
            configuredExecutablePath,
            startTimeUtc);

    internal static bool MatchesOpenProcessInstance(
        ProcessIdentity expected,
        DateTimeOffset actualStartTimeUtc) =>
        expected.StartTimeUtc == actualStartTimeUtc;

    internal static string ParseServiceExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new UpgradePreflightException("WebAssistant service binary path is empty.");
        }

        var value = commandLine.Trim();
        string executable;

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                throw new UpgradePreflightException(
                    "WebAssistant service binary path has an unterminated quoted executable.");
            }

            executable = value[1..closingQuote];
        }
        else
        {
            var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0)
            {
                throw new UpgradePreflightException(
                    "WebAssistant service binary path does not contain an executable path.");
            }

            executable = value[..(exeEnd + 4)];
        }

        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpgradePreflightException(
                "WebAssistant service executable path must end with .exe.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return executable.Replace('/', '\\');
        }

        return Path.GetFullPath(executable);
    }

    private static SafeServiceHandle? OpenWebAssistantService(uint desiredAccess, bool allowMissing)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw Win32Failure("OpenSCManager");
        }

        var service = OpenService(manager, ServiceName, desiredAccess);
        if (!service.IsInvalid)
        {
            return service;
        }

        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (allowMissing && error == ErrorServiceDoesNotExist)
        {
            return null;
        }

        throw Win32Failure("OpenService(WebAssistant)", error);
    }

    private static string QueryConfiguredExecutablePath(SafeServiceHandle service)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
        var error = Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 || error != ErrorInsufficientBuffer)
        {
            throw Win32Failure("QueryServiceConfig(size)", error);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                throw Win32Failure("QueryServiceConfig");
            }

            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
            var binaryPath = Marshal.PtrToStringUni(config.BinaryPathName);
            return ParseServiceExecutablePath(binaryPath ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SERVICE_STATUS_PROCESS QueryStatus(SafeServiceHandle service)
    {
        var size = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                size,
                out _))
        {
            throw Win32Failure("QueryServiceStatusEx");
        }

        return status;
    }

    private static ServiceState MapServiceState(uint state) => state switch
    {
        NativeServiceStopped => ServiceState.Stopped,
        NativeServiceStartPending => ServiceState.StartPending,
        NativeServiceStopPending => ServiceState.StopPending,
        NativeServiceRunning => ServiceState.Running,
        _ => throw new UpgradePreflightException(
            $"WebAssistant service is in unsupported state {state}; preflight refuses to guess.")
    };

    private static int FindParentProcessId(uint processId)
    {
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw Win32Failure("CreateToolhelp32Snapshot");
        }

        var entry = PROCESSENTRY32.Create();
        if (!Process32First(snapshot, ref entry))
        {
            throw Win32Failure("Process32First");
        }

        do
        {
            if (entry.ProcessId == processId)
            {
                return checked((int)entry.ParentProcessId);
            }

            entry = PROCESSENTRY32.Create();
        }
        while (Process32Next(snapshot, ref entry));

        throw new UpgradePreflightException(
            $"SCM reported WebAssistant pid={processId}, but it is absent from the process snapshot.");
    }

    private static ProcessIdentity? TryReadServiceProcessIdentity(
        int processId,
        int parentProcessId,
        string configuredExecutablePath,
        bool allowInaccessible)
    {
        using var handle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidParameter || (allowInaccessible && error == ErrorAccessDenied))
            {
                return null;
            }

            throw Win32Failure($"OpenProcess(pid={processId})", error);
        }

        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            throw Win32Failure($"GetProcessTimes(pid={processId})");
        }

        return CreateServiceProcessIdentity(
            processId,
            parentProcessId,
            configuredExecutablePath,
            DateTimeOffset.FromFileTime(creation.ToLong()));
    }

    private static ProcessIdentity? TryReadProcessIdentity(
        int processId,
        int parentProcessId,
        bool allowInaccessible)
    {
        using var handle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidParameter || (allowInaccessible && error == ErrorAccessDenied))
            {
                return null;
            }

            throw Win32Failure($"OpenProcess(pid={processId})", error);
        }

        return ReadProcessIdentity(handle, processId, parentProcessId);
    }

    private static ProcessIdentity ReadProcessIdentity(
        SafeKernelHandle handle,
        int processId,
        int parentProcessId)
    {
        var capacity = 32768;
        var path = new StringBuilder(capacity);
        var size = capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size))
        {
            throw Win32Failure($"QueryFullProcessImageName(pid={processId})");
        }

        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            throw Win32Failure($"GetProcessTimes(pid={processId})");
        }

        var startTimeUtc = DateTimeOffset.FromFileTime(creation.ToLong());
        return new ProcessIdentity(
            processId,
            parentProcessId,
            Path.GetFullPath(path.ToString()),
            startTimeUtc);
    }

    private static SafeKernelHandle? OpenExactProcess(
        ProcessIdentity expected,
        uint desiredAccess,
        bool allowGone)
    {
        var handle = OpenProcess(desiredAccess, false, checked((uint)expected.ProcessId));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (allowGone && error == ErrorInvalidParameter)
            {
                return null;
            }

            throw Win32Failure($"OpenProcess(pid={expected.ProcessId})", error);
        }

        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw Win32Failure($"GetProcessTimes(pid={expected.ProcessId})", error);
        }

        var actualStartTimeUtc = DateTimeOffset.FromFileTime(creation.ToLong());
        if (!MatchesOpenProcessInstance(expected, actualStartTimeUtc))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WebAssistant UpgradePreflight may execute only on Windows.");
        }
    }

    private static UpgradePreflightException Win32Failure(string operation, int? errorOverride = null)
    {
        var error = errorOverride ?? Marshal.GetLastWin32Error();
        var message = new Win32Exception(error).Message;
        return new UpgradePreflightException(
            $"{operation} failed with Win32 error {error}: {message}");
    }

    private const uint NativeServiceStopped = 0x00000001;
    private const uint NativeServiceStartPending = 0x00000002;
    private const uint NativeServiceStopPending = 0x00000003;
    private const uint NativeServiceRunning = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;

        internal static PROCESSENTRY32 Create() => new()
        {
            Size = checked((uint)Marshal.SizeOf<PROCESSENTRY32>()),
            ExeFile = string.Empty
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;

        internal long ToLong() => unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeKernelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out SERVICE_STATUS_PROCESS status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        SafeKernelHandle snapshot,
        ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        SafeKernelHandle snapshot,
        ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeKernelHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeKernelHandle process,
        out FILETIME creationTime,
        out FILETIME exitTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
