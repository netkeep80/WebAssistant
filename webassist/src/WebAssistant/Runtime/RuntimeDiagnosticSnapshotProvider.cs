using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAPS2.Scan;

namespace WebAssistant.Runtime;

internal sealed class RuntimeDiagnosticSnapshotProvider
{
    private readonly ConcurrentDictionary<FileCacheKey, RuntimeFileFingerprint> fileCache = new();
    private readonly Lazy<PackageFingerprintSnapshot> packageFingerprint;

    internal RuntimeDiagnosticSnapshotProvider()
    {
        packageFingerprint = new Lazy<PackageFingerprintSnapshot>(
            CapturePackageFingerprint,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal RuntimeDiagnosticSnapshot Capture()
    {
        var package = packageFingerprint.Value;
        return new RuntimeDiagnosticSnapshot(
            package.CapturedAtUtc,
            CaptureCurrentProcess(),
            package.Components,
            CaptureWorkers());
    }

    internal IReadOnlyList<RuntimeWorkerIdentity> CaptureWorkers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var parentIds = TrySnapshotParentProcessIds();
        var workers = new List<RuntimeWorkerIdentity>();

        foreach (var process in Process.GetProcessesByName("NAPS2.Worker"))
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) ||
                        !IsUnderDirectory(executablePath, AppContext.BaseDirectory))
                    {
                        continue;
                    }

                    int? parentPid = null;
                    if (parentIds is not null &&
                        parentIds.TryGetValue(process.Id, out var resolvedParentPid))
                    {
                        parentPid = resolvedParentPid;
                        if (resolvedParentPid != Environment.ProcessId)
                        {
                            continue;
                        }
                    }

                    var executable = CaptureFile(executablePath);
                    var modules = CaptureTwainModules(
                        process,
                        out var moduleInspectionState,
                        out var moduleInspectionErrorType);

                    workers.Add(new RuntimeWorkerIdentity(
                        process.Id,
                        parentPid,
                        executable.Architecture,
                        executable.FilePath,
                        executable.FileVersion,
                        executable.ProductVersion,
                        executable.Size,
                        executable.Sha256,
                        TryGetStartTimeUtc(process),
                        moduleInspectionState,
                        moduleInspectionErrorType,
                        modules));
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return workers
            .OrderBy(worker => worker.Pid)
            .ToArray();
    }

    private PackageFingerprintSnapshot CapturePackageFingerprint()
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var components = new[]
        {
            CaptureComponent(
                "WebAssistant",
                ResolveWebAssistantBinaryPath()),
            CaptureComponent(
                "NAPS2.Sdk",
                typeof(ScanningContext).Assembly.Location),
            CaptureComponent(
                "NAPS2.Worker",
                ResolveWorkerBinaryPath())
        };

        return new PackageFingerprintSnapshot(capturedAtUtc, components);
    }

    private RuntimeComponentIdentity CaptureComponent(
        string name,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new RuntimeComponentIdentity(
                name,
                Available: false,
                FilePath: path,
                FileVersion: null,
                ProductVersion: null,
                AssemblyVersion: null,
                Size: null,
                Sha256: null,
                Architecture: null);
        }

        var file = CaptureFile(path);
        return new RuntimeComponentIdentity(
            name,
            Available: true,
            file.FilePath,
            file.FileVersion,
            file.ProductVersion,
            file.AssemblyVersion,
            file.Size,
            file.Sha256,
            file.Architecture);
    }

    private RuntimeFileFingerprint CaptureFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        var key = new FileCacheKey(
            fullPath,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);

        return fileCache.GetOrAdd(
            key,
            static cacheKey => ComputeFileFingerprint(cacheKey.Path));
    }

    private static RuntimeFileFingerprint ComputeFileFingerprint(string path)
    {
        var info = new FileInfo(path);
        var version = FileVersionInfo.GetVersionInfo(path);

        string? assemblyVersion = null;
        try
        {
            assemblyVersion = AssemblyName.GetAssemblyName(path)
                .Version?
                .ToString();
        }
        catch (BadImageFormatException)
        {
        }
        catch (FileLoadException)
        {
        }

        using var stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var sha256 = Convert
            .ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();

        return new RuntimeFileFingerprint(
            Path.GetFullPath(path),
            NullIfWhiteSpace(version.FileVersion),
            NullIfWhiteSpace(version.ProductVersion),
            assemblyVersion,
            info.Length,
            sha256,
            DetectBinaryArchitecture(path));
    }

    private IReadOnlyList<RuntimeModuleIdentity> CaptureTwainModules(
        Process process,
        out string inspectionState,
        out string? inspectionErrorType)
    {
        try
        {
            var modules = new List<RuntimeModuleIdentity>();
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(
                        module.ModuleName,
                        "twaindsm.dll",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = module.FileName;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var file = CaptureFile(path);
                modules.Add(new RuntimeModuleIdentity(
                    module.ModuleName,
                    file.FilePath,
                    file.FileVersion,
                    file.ProductVersion,
                    file.Size,
                    file.Sha256,
                    file.Architecture));
            }

            inspectionState = "ok";
            inspectionErrorType = null;
            return modules;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            inspectionState = "unavailable";
            inspectionErrorType = exception.GetType().Name;
            return [];
        }
    }

    private static RuntimeProcessIdentity CaptureCurrentProcess()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            using var process = Process.GetCurrentProcess();
            executablePath = process.MainModule?.FileName;
        }

        return new RuntimeProcessIdentity(
            Environment.ProcessId,
            NormalizeArchitecture(RuntimeInformation.ProcessArchitecture),
            executablePath ?? "unknown",
            RuntimeInformation.FrameworkDescription);
    }

    private static string ResolveWebAssistantBinaryPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "WebAssistant",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return typeof(AgentRuntimeInfo).Assembly.Location;
    }

    private static string? ResolveWorkerBinaryPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "NAPS2.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "lib", "NAPS2.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "_win32", "NAPS2.Worker.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string DetectBinaryArchitecture(string path)
    {
        try
        {
            using var stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            var machine = headers.CoffHeader.Machine;

            if (machine == Machine.I386 &&
                headers.CorHeader is not null &&
                (headers.CorHeader.Flags & CorFlags.Requires32Bit) == 0)
            {
                return "anycpu";
            }

            return machine switch
            {
                Machine.I386 => "x86",
                Machine.Amd64 => "x64",
                Machine.Arm => "arm",
                Machine.ArmThumb2 => "arm",
                Machine.Arm64 => "arm64",
                _ => machine.ToString().ToLowerInvariant()
            };
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or
            IOException or
            UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static string NormalizeArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => architecture.ToString().ToLowerInvariant()
        };

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static Dictionary<int, int>? TrySnapshotParentProcessIds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return fullPath.StartsWith(
            fullDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        nint snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        nint snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private sealed record PackageFingerprintSnapshot(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<RuntimeComponentIdentity> Components);

    private sealed record RuntimeFileFingerprint(
        string FilePath,
        string? FileVersion,
        string? ProductVersion,
        string? AssemblyVersion,
        long Size,
        string Sha256,
        string Architecture);

    private sealed record FileCacheKey(
        string Path,
        long Size,
        long LastWriteTimeUtcTicks);
}

internal sealed record RuntimeDiagnosticSnapshot(
    DateTimeOffset PackageCapturedAtUtc,
    RuntimeProcessIdentity Process,
    IReadOnlyList<RuntimeComponentIdentity> Components,
    IReadOnlyList<RuntimeWorkerIdentity> Workers);

internal sealed record RuntimeProcessIdentity(
    int Pid,
    string Architecture,
    string ExecutablePath,
    string Framework);

internal sealed record RuntimeComponentIdentity(
    string Name,
    bool Available,
    string? FilePath,
    string? FileVersion,
    string? ProductVersion,
    string? AssemblyVersion,
    long? Size,
    string? Sha256,
    string? Architecture);

internal sealed record RuntimeWorkerIdentity(
    int Pid,
    int? ParentPid,
    string Architecture,
    string ExecutablePath,
    string? FileVersion,
    string? ProductVersion,
    long Size,
    string Sha256,
    DateTimeOffset? StartedAtUtc,
    string ModuleInspectionState,
    string? ModuleInspectionErrorType,
    IReadOnlyList<RuntimeModuleIdentity> TwainModules);

internal sealed record RuntimeModuleIdentity(
    string Name,
    string FilePath,
    string? FileVersion,
    string? ProductVersion,
    long Size,
    string Sha256,
    string Architecture);
