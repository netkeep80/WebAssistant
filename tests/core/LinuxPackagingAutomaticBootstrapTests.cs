using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxPackagingAutomaticBootstrapTests
{
    [Fact]
    public void LinuxPackage_WithoutSdk_AutomaticallyBootstrapsDotnet10ByDefault()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory();
        var fakeBin = Path.Combine(sandbox.Path, "bin");
        var installer = Path.Combine(sandbox.Path, "dotnet-install-source.sh");
        var bootstrapRoot = Path.Combine(sandbox.Path, "bootstrap-sdk");
        var publishMarker = Path.Combine(sandbox.Path, "publish.marker");
        var curlMarker = Path.Combine(sandbox.Path, "curl.marker");
        var aptMarker = Path.Combine(sandbox.Path, "apt.marker");
        var sudoMarker = Path.Combine(sandbox.Path, "sudo.marker");
        var output = Path.Combine(sandbox.Path, "package");

        Directory.CreateDirectory(fakeBin);
        WriteFakeDotnet(Path.Combine(fakeBin, "dotnet"), "9.0.999", publishMarker);
        WriteFakeInstaller(installer, publishMarker);
        WriteFakeCurl(Path.Combine(fakeBin, "curl"), installer, curlMarker);
        WriteMarkerCommand(Path.Combine(fakeBin, "apt-get"), aptMarker, 87);
        WriteMarkerCommand(Path.Combine(fakeBin, "sudo"), sudoMarker, 86);

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
        startInfo.Environment["WEBASSISTANT_DOTNET_ROOT"] = "";
        startInfo.Environment["WEBASSISTANT_DOTNET_INSTALL_DIR"] = bootstrapRoot;
        startInfo.Environment.Remove("WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start package.sh.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"package.sh failed. stdout: {stdout}\nstderr: {stderr}");
        Assert.True(File.Exists(curlMarker), "Default SDK resolution must download the official installer when SDK 10 is absent.");
        Assert.True(File.Exists(publishMarker), "Bootstrapped SDK must perform the canonical publish.");
        Assert.False(File.Exists(aptMarker), "Automatic bootstrap must not use apt-get.");
        Assert.False(File.Exists(sudoMarker), "Automatic bootstrap must not elevate into a package manager.");
    }

    private static void WriteFakeDotnet(string path, string sdkVersion, string publishMarker)
    {
        var script = $$"""
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--list-sdks" ]]; then
    echo "{{sdkVersion}}.100 [/fake/sdk]"
    exit 0
fi
printf '%s\n' "$*" >> '{{publishMarker}}'
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

    private static void WriteFakeInstaller(string path, string publishMarker)
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
printf '%s\n' "$*" >> '{{publishMarker}}'
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
        WriteExecutable(path, $"#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> '{marker}'\nexit {exitCode}\n");
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"webassistant-auto-bootstrap-test-{Guid.NewGuid():N}");
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
