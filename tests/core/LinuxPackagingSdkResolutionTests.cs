using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxPackagingSdkResolutionTests
{
    [Fact]
    public void LinuxPackage_PrefersExplicitLocalSdkOverSystemSdk()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var localSdkRoot = Path.Combine(sandbox.Path, "local-sdk");
        var fakeBin = Path.Combine(sandbox.Path, "bin");
        var localMarker = Path.Combine(sandbox.Path, "local.marker");
        var systemMarker = Path.Combine(sandbox.Path, "system.marker");
        var output = Path.Combine(sandbox.Path, "package");

        Directory.CreateDirectory(localSdkRoot);
        Directory.CreateDirectory(fakeBin);
        WriteFakeDotnet(Path.Combine(localSdkRoot, "dotnet"), localMarker, "10.0.999");
        WriteFakeDotnet(Path.Combine(fakeBin, "dotnet"), systemMarker, "10.0.999");

        var result = RunPackage(
            output,
            fakeBin,
            new Dictionary<string, string>
            {
                ["WEBASSISTANT_DOTNET_ROOT"] = localSdkRoot,
                ["WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP"] = "0"
            });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(localMarker), $"Explicit local SDK was not used. stderr: {result.StandardError}");
        Assert.Contains("publish", File.ReadAllText(localMarker));
        Assert.False(File.Exists(systemMarker), "System SDK must not be probed when an explicit valid local SDK is provided.");
    }

    [Fact]
    public void LinuxPackage_UsesInstalledSystemSdk10WithoutBootstrap()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var fakeBin = Path.Combine(sandbox.Path, "bin");
        var systemMarker = Path.Combine(sandbox.Path, "system.marker");
        var curlMarker = Path.Combine(sandbox.Path, "curl.marker");
        var aptMarker = Path.Combine(sandbox.Path, "apt.marker");
        var output = Path.Combine(sandbox.Path, "package");

        Directory.CreateDirectory(fakeBin);
        WriteFakeDotnet(Path.Combine(fakeBin, "dotnet"), systemMarker, "10.0.999");
        WriteMarkerCommand(Path.Combine(fakeBin, "curl"), curlMarker, 88);
        WriteMarkerCommand(Path.Combine(fakeBin, "apt-get"), aptMarker, 87);

        var result = RunPackage(
            output,
            fakeBin,
            new Dictionary<string, string>
            {
                ["WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP"] = "0",
                ["WEBASSISTANT_DOTNET_ROOT"] = ""
            });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(systemMarker), $"Installed system SDK 10 was not used. stderr: {result.StandardError}");
        Assert.Contains("publish", File.ReadAllText(systemMarker));
        Assert.False(File.Exists(curlMarker), "System SDK resolution must not invoke online bootstrap.");
        Assert.False(File.Exists(aptMarker), "System SDK resolution must not invoke apt.");
    }

    [Fact]
    public void LinuxPackage_WithoutSdkAndWithoutBootstrap_FailsWithoutPackageManagerOrNetwork()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var fakeBin = Path.Combine(sandbox.Path, "bin");
        var aptMarker = Path.Combine(sandbox.Path, "apt.marker");
        var sudoMarker = Path.Combine(sandbox.Path, "sudo.marker");
        var curlMarker = Path.Combine(sandbox.Path, "curl.marker");
        var output = Path.Combine(sandbox.Path, "package");

        Directory.CreateDirectory(fakeBin);
        WriteFakeDotnet(Path.Combine(fakeBin, "dotnet"), Path.Combine(sandbox.Path, "system.marker"), "9.0.999");
        WriteMarkerCommand(Path.Combine(fakeBin, "apt-get"), aptMarker, 87);
        WriteMarkerCommand(Path.Combine(fakeBin, "sudo"), sudoMarker, 86);
        WriteMarkerCommand(Path.Combine(fakeBin, "curl"), curlMarker, 88);

        var result = RunPackage(
            output,
            fakeBin,
            new Dictionary<string, string>
            {
                ["WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP"] = "0",
                ["WEBASSISTANT_DOTNET_ROOT"] = ""
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=1", result.StandardError);
        Assert.False(File.Exists(aptMarker), "package.sh must not use apt to obtain .NET SDK 10.");
        Assert.False(File.Exists(sudoMarker), "package.sh must not elevate into a package manager to obtain .NET SDK 10.");
        Assert.False(File.Exists(curlMarker), "Network bootstrap must not run without explicit opt-in.");
    }

    [Fact]
    public void LinuxPackage_WithExplicitBootstrap_UsesDotnetInstallWithoutApt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var fakeBin = Path.Combine(sandbox.Path, "bin");
        var installer = Path.Combine(sandbox.Path, "dotnet-install-source.sh");
        var bootstrapRoot = Path.Combine(sandbox.Path, "bootstrap-sdk");
        var bootstrapMarker = Path.Combine(sandbox.Path, "bootstrap.marker");
        var curlMarker = Path.Combine(sandbox.Path, "curl.marker");
        var aptMarker = Path.Combine(sandbox.Path, "apt.marker");
        var sudoMarker = Path.Combine(sandbox.Path, "sudo.marker");
        var output = Path.Combine(sandbox.Path, "package");

        Directory.CreateDirectory(fakeBin);
        WriteFakeDotnet(Path.Combine(fakeBin, "dotnet"), Path.Combine(sandbox.Path, "system.marker"), "9.0.999");
        WriteFakeDotnetInstaller(installer, bootstrapMarker);
        WriteFakeCurl(Path.Combine(fakeBin, "curl"), installer, curlMarker);
        WriteMarkerCommand(Path.Combine(fakeBin, "apt-get"), aptMarker, 87);
        WriteMarkerCommand(Path.Combine(fakeBin, "sudo"), sudoMarker, 86);

        var result = RunPackage(
            output,
            fakeBin,
            new Dictionary<string, string>
            {
                ["WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP"] = "1",
                ["WEBASSISTANT_DOTNET_INSTALL_DIR"] = bootstrapRoot,
                ["WEBASSISTANT_DOTNET_ROOT"] = ""
            });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(curlMarker), $"Explicit bootstrap did not download dotnet-install.sh. stderr: {result.StandardError}");
        Assert.True(File.Exists(bootstrapMarker), $"Bootstrapped SDK was not used. stderr: {result.StandardError}");
        Assert.Contains("publish", File.ReadAllText(bootstrapMarker));
        Assert.False(File.Exists(aptMarker), "Online bootstrap must not use apt to obtain .NET SDK 10.");
        Assert.False(File.Exists(sudoMarker), "Online bootstrap must not elevate into apt to obtain .NET SDK 10.");
    }

    private static ProcessResult RunPackage(
        string output,
        string fakeBin,
        IReadOnlyDictionary<string, string> environment)
    {
        var packageScript = Path.Combine(FindRepositoryRoot(), "webassist", "build", "linux", "package.sh");
        var startInfo = new ProcessStartInfo("/usr/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(packageScript);
        startInfo.ArgumentList.Add(output);
        startInfo.Environment["PATH"] = $"{fakeBin}:/usr/bin:/bin";

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start package.sh.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void WriteFakeDotnet(string path, string marker, string sdkVersion)
    {
        var script = $$"""
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "{{sdkVersion}}.100 [/fake/sdk]"
    exit 0
fi
printf '%s\n' "$*" >> '{{marker}}'
if [[ "${1:-}" == "publish" ]]; then
    output=""
    while (($#)); do
        if [[ "$1" == "--output" ]]; then
            shift
            output="$1"
            break
        fi
        shift
    done
    [[ -n "$output" ]]
    mkdir -p "$output"
    printf '#!/usr/bin/env bash\nexit 0\n' > "$output/WebAssistant"
    chmod +x "$output/WebAssistant"
fi
""";
        WriteExecutable(path, script);
    }

    private static void WriteFakeDotnetInstaller(string path, string marker)
    {
        var script = $$"""
#!/usr/bin/env bash
set -euo pipefail
install_dir=""
while (($#)); do
    if [[ "$1" == "--install-dir" ]]; then
        shift
        install_dir="$1"
        break
    fi
    shift
done
[[ -n "$install_dir" ]]
mkdir -p "$install_dir"
cat > "$install_dir/dotnet" <<'DOTNET'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "10.0.999 [/fake/sdk]"
    exit 0
fi
printf '%s\n' "$*" >> '{{marker}}'
if [[ "${1:-}" == "publish" ]]; then
    output=""
    while (($#)); do
        if [[ "$1" == "--output" ]]; then
            shift
            output="$1"
            break
        fi
        shift
    done
    [[ -n "$output" ]]
    mkdir -p "$output"
    printf '#!/usr/bin/env bash\nexit 0\n' > "$output/WebAssistant"
    chmod +x "$output/WebAssistant"
fi
DOTNET
chmod +x "$install_dir/dotnet"
""";
        WriteExecutable(path, script);
    }

    private static void WriteFakeCurl(string path, string installerSource, string marker)
    {
        var script = $$"""
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> '{{marker}}'
output=""
while (($#)); do
    if [[ "$1" == "-o" ]]; then
        shift
        output="$1"
        break
    fi
    shift
done
[[ -n "$output" ]]
cp '{{installerSource}}' "$output"
chmod +x "$output"
""";
        WriteExecutable(path, script);
    }

    private static void WriteMarkerCommand(string path, string marker, int exitCode)
    {
        WriteExecutable(
            path,
            $"#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> '{marker}'\nexit {exitCode}\n");
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
#pragma warning disable CA1416
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "webassist", "WebAssistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("WebAssistant repository root was not found.");
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"webassistant-sdk-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
