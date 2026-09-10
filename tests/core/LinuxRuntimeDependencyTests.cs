using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxRuntimeDependencyTests
{
    [Fact]
    public void ALT10_1_Icu69AndExistingGtkSane_SucceedsWithoutAptMutation()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AllCapabilitiesPresent);
        var result = fixture.RunHelper();

        Assert.True(result.ExitCode == 0, $"ALT 10.1 ICU 69 fixture must be accepted. stderr: {result.Error}\nstdout: {result.Output}");
        Assert.False(File.Exists(fixture.AptLog), "Satisfied runtime capabilities must not mutate apt-rpm state.");
    }

    [Fact]
    public void MissingIcu_ResolvesAvailableMajorFromAptCacheWithoutHardCodedMajor()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.MissingIcuResolvable);
        var result = fixture.RunHelper();

        Assert.True(result.ExitCode == 0, $"Resolvable ICU capability must be installed dynamically. stderr: {result.Error}\nstdout: {result.Output}");
        var apt = File.ReadAllText(fixture.AptLog);
        Assert.Contains("update", apt, StringComparison.Ordinal);
        Assert.Contains("install -y libicu69", apt, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.InstallMarker), "ICU install fixture was not exercised.");
    }

    [Fact]
    public void MissingIcu_WithNoResolvablePackage_FailsActionablyWithoutAptMutation()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.MissingIcuUnresolvable);
        var result = fixture.RunHelper();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ICU", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не найд", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.AptLog), "Installer must not mutate apt when ICU package resolution is impossible.");
    }

    [Fact]
    public void RuntimeDependencyContract_ContainsNoHardCodedLibicuMajor()
    {
        var root = FindRepositoryRoot();
        var helper = Path.Combine(root, "webassist", "install", "linux", "runtime-dependencies.sh");
        Assert.True(File.Exists(helper), $"Runtime dependency helper is missing: {helper}");

        var text = File.ReadAllText(helper);
        Assert.DoesNotMatch(new Regex(@"libicu\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), text);
        Assert.Contains("ensure_webassistant_runtime_dependencies", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }

    private enum RuntimeFixtureMode
    {
        AllCapabilitiesPresent,
        MissingIcuResolvable,
        MissingIcuUnresolvable
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class RuntimeFixture : IDisposable
    {
        private RuntimeFixture(string root, string binDirectory, string aptLog, string installMarker)
        {
            Root = root;
            BinDirectory = binDirectory;
            AptLog = aptLog;
            InstallMarker = installMarker;
        }

        public string Root { get; }
        public string BinDirectory { get; }
        public string AptLog { get; }
        public string InstallMarker { get; }

        public static RuntimeFixture Create(RuntimeFixtureMode mode)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-alt-runtime-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var aptLog = Path.Combine(root, "apt.log");
            var installMarker = Path.Combine(root, "icu-installed");

            WriteExecutable(Path.Combine(bin, "ldconfig"), mode switch
            {
                RuntimeFixtureMode.AllCapabilitiesPresent => """
#!/usr/bin/env bash
cat <<'OUT'
libicuuc.so.69
libicui18n.so.69
libicudata.so.69
libgtk-3.so.0
libsane.so.1
OUT
""",
                _ => $$"""
#!/usr/bin/env bash
if [[ -f '{{installMarker}}' ]]; then
  echo 'libicuuc.so.69'
  echo 'libicui18n.so.69'
  echo 'libicudata.so.69'
fi
echo 'libgtk-3.so.0'
echo 'libsane.so.1'
"""
            });

            WriteExecutable(Path.Combine(bin, "scanimage"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(bin, "apt-cache"), mode == RuntimeFixtureMode.MissingIcuResolvable
                ? "#!/usr/bin/env bash\n[[ \"${1:-}\" == 'pkgnames' ]] || exit 90\nprintf '%s\\n' libicu-data libicu69\n"
                : "#!/usr/bin/env bash\n[[ \"${1:-}\" == 'pkgnames' ]] || exit 90\nprintf '%s\\n' libicu-data\n");

            WriteExecutable(Path.Combine(bin, "apt-get"), $$"""
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> '{{aptLog}}'
if [[ "${1:-}" == 'update' ]]; then
  exit 0
fi
if [[ "${1:-}" == 'install' && "$*" == *'libicu69'* ]]; then
  touch '{{installMarker}}'
  exit 0
fi
exit 91
""");

            return new RuntimeFixture(root, bin, aptLog, installMarker);
        }

        public ProcessResult RunHelper()
        {
            var helper = Path.Combine(FindRepositoryRoot(), "webassist", "install", "linux", "runtime-dependencies.sh");
            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("source \"$RUNTIME_HELPER\"; ensure_webassistant_runtime_dependencies");
            startInfo.Environment["RUNTIME_HELPER"] = helper;
            startInfo.Environment["PATH"] = BinDirectory + Path.PathSeparator + "/usr/bin:/bin";

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить runtime dependency helper.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, output, error);
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

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
