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
    public void MissingAirscan_InstallsOnlyMissingPackageAndRechecks()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.MissingAirscan);
        var result = fixture.RunHelper();
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(new[] { "update", "install -y sane-airscan" }, File.ReadAllLines(fixture.AptLog));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(fixture.Root, "sane.d", "dll.d", "airscan")));
        Assert.False(Directory.Exists(fixture.Pending));
        Assert.True(fixture.RunHelper().ExitCode == 0);
        Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.AirscanAptFailure)]
    [InlineData(RuntimeFixtureMode.AirscanIncompleteInstall)]
    public void MissingAirscan_FailedOrIncompleteInstallation_FailsActionably(RuntimeFixtureMode mode)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(mode);
        var result = fixture.RunHelper();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sane-airscan", result.Error, StringComparison.Ordinal);
        Assert.Contains("install -y sane-airscan", File.ReadAllText(fixture.AptLog), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.AirscanBroken)]
    [InlineData(RuntimeFixtureMode.AirscanEmpty)]
    [InlineData(RuntimeFixtureMode.AirscanWrongArch)]
    [InlineData(RuntimeFixtureMode.AirscanMissingDependency)]
    [InlineData(RuntimeFixtureMode.AirscanMissingDiscover)]
    public void InstalledButBrokenAirscan_FailsWithoutAptMutation(RuntimeFixtureMode mode)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fixture = RuntimeFixture.Create(mode);
        var result = fixture.RunHelper();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sane-airscan", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.AptLog));
    }

    [Fact]
    public void AirscanMetadataFailure_NamesMissingPackage()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanUpdateFailure);
        var result = fixture.RunHelper();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sane-airscan", result.Error, StringComparison.Ordinal);
        Assert.Equal(new[] { "update" }, File.ReadAllLines(fixture.AptLog));
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.AirscanEmpty, "dlopen:")]
    [InlineData(RuntimeFixtureMode.AirscanWrongArch, "dlopen:")]
    [InlineData(RuntimeFixtureMode.AirscanMissingDependency, "dlopen:")]
    [InlineData(RuntimeFixtureMode.AirscanMissingSymbols, "dlsym sane_airscan_")]
    public void AirscanLoader_RejectsInvalidLibraryWithSpecificDiagnostic(RuntimeFixtureMode mode, string diagnostic)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(mode);
        var result = fixture.RunLoader();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AirscanLoader_AcceptsRealElfWithAllRequiredSymbols()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AllCapabilitiesPresent);
        var result = fixture.RunLoader();
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("dlopen(RTLD_NOW)/dlsym: PASS", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AirscanLoader_RejectsMissingServiceUserBeforeLoading()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AllCapabilitiesPresent);
        var result = fixture.RunLoader("webassistant-fixture-missing-user");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("UID", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.MissingLoaderProbe, true)]
    [InlineData(RuntimeFixtureMode.IncompleteLoaderProbe, false)]
    public void LoaderProbeDependency_InstalledOnlyWhenMissingAndRechecked(RuntimeFixtureMode mode, bool success)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(mode);
        var result = fixture.RunHelper();
        Assert.Equal(success, result.ExitCode == 0);
        Assert.Equal(new[] { "update", "install -y python3" }, File.ReadAllLines(fixture.AptLog));
        if (success)
        {
            Assert.Equal(0, fixture.RunHelper().ExitCode);
            Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
        }
        else
            Assert.Contains("python3/ctypes", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.MissingRunuser, true)]
    [InlineData(RuntimeFixtureMode.IncompleteRunuser, false)]
    public void ServiceRunner_InstalledOnlyWhenMissingAndRechecked(RuntimeFixtureMode mode, bool success)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(mode);
        var result = fixture.RunHelper();
        Assert.Equal(success, result.ExitCode == 0);
        Assert.Equal(new[] { "update", "install -y util-linux" }, File.ReadAllLines(fixture.AptLog));
        if (success)
        {
            Assert.Equal(0, fixture.RunHelper().ExitCode);
            Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
        }
        else
            Assert.Contains("runuser", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeFixtureMode.AirscanDisabled, true)]
    [InlineData(RuntimeFixtureMode.AirscanDisabledPartialFailure, false)]
    public void DisabledAirscan_PreservesAdministrativeFileAndArchivesNewRpmnew(RuntimeFixtureMode mode, bool success)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(mode);
        var directory = Path.Combine(fixture.Root, "sane.d", "dll.d");
        var result = fixture.RunHelper();
        Assert.Equal(success, result.ExitCode == 0);
        Assert.Equal("# airscan\n", File.ReadAllText(Path.Combine(directory, "airscan")));
        Assert.False(File.Exists(Path.Combine(directory, "airscan.rpmnew")));
        var archive = Assert.Single(Directory.GetDirectories(directory, ".webassistant-airscan-rpmnew.*"));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(archive, "airscan.rpmnew")));
        if (success)
        {
            Assert.Equal(0, fixture.RunHelper().ExitCode);
            Assert.Single(Directory.GetDirectories(directory, ".webassistant-airscan-rpmnew.*"));
            Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
        }
    }

    [Fact]
    public void DisabledAirscan_PreexistingRpmnewIsNotOverwritten()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanDisabledExistingRpmnew);
        var result = fixture.RunHelper();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("airscan.rpmnew", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.AptLog));
        Assert.Equal("# existing\n", File.ReadAllText(Path.Combine(fixture.Root, "sane.d", "dll.d", "airscan.rpmnew")));
    }

    [Theory]
    [InlineData("airscan\n", false)]
    [InlineData("\"airscan\"\n", false)]
    [InlineData("\"airscan\"\n", true)]
    public void ExistingRegistration_IsPreservedWithoutInterpretingSyntaxOrPermissions(string contents, bool privateFile)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanEnabled);
        var directory = Path.Combine(fixture.Root, "sane.d", "dll.d");
        var path = Path.Combine(directory, "airscan");
        File.WriteAllText(path, contents);
        if (privateFile)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var permissions = File.GetUnixFileMode(path);
        var result = fixture.RunHelper();
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Equal(permissions, File.GetUnixFileMode(path));
        Assert.False(File.Exists(Path.Combine(directory, "airscan.rpmnew")));
        var archive = Assert.Single(Directory.GetDirectories(directory, ".webassistant-airscan-rpmnew.*"));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(archive, "airscan.rpmnew")));
    }

    [Fact]
    public void ArchiveFailure_AfterSuccessfulRpmInstall_BlocksRetryWithoutMovingUnknownFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanDisabled);
        var mv = Path.Combine(fixture.BinDirectory, "mv");
        File.WriteAllText(mv, "#!/bin/sh\nexit 96\n");
        File.SetUnixFileMode(mv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var directory = Path.Combine(fixture.Root, "sane.d", "dll.d");
        var first = fixture.RunHelper();
        Assert.NotEqual(0, first.ExitCode);
        Assert.Contains("Не удалось сохранить новый файл RPM", first.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "airscan-installed")));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(directory, "airscan.rpmnew")));
        File.Delete(mv);
        var retry = fixture.RunHelper();
        Assert.NotEqual(0, retry.ExitCode);
        Assert.Contains("Неразрешённый файл", retry.Error, StringComparison.Ordinal);
        Assert.Equal(new[] { "update", "install -y sane-airscan" }, File.ReadAllLines(fixture.AptLog));
        Assert.Equal("# airscan\n", File.ReadAllText(Path.Combine(directory, "airscan")));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(directory, "airscan.rpmnew")));
    }

    [Fact]
    public void InterruptedTransaction_AlreadyInstalledPackageWithRpmnew_BlocksBeforeApt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AllCapabilitiesPresent);
        var directory = Path.Combine(fixture.Root, "sane.d", "dll.d");
        File.WriteAllText(Path.Combine(directory, "airscan"), "# airscan\n");
        File.WriteAllText(Path.Combine(directory, "airscan.rpmnew"), "airscan\n");
        var result = fixture.RunHelper();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Неразрешённый файл", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.AptLog));
        Assert.Equal("airscan\n", File.ReadAllText(Path.Combine(directory, "airscan.rpmnew")));
        Assert.Empty(Directory.GetDirectories(directory, ".webassistant-airscan-rpmnew.*"));
    }

    [Theory]
    [InlineData("chmod 0644 \"$registration\"")]
    [InlineData("printf '%s\\n' changed > \"$registration\"")]
    [InlineData("rm -- \"$registration\"")]
    public void ChangedRegistration_WithoutRpmnew_BlocksRetryUntilAdministrativeResolution(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanEnabled);
        var registration = Path.Combine(fixture.Root, "sane.d", "dll.d", "airscan");
        File.SetUnixFileMode(registration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        fixture.AfterAptInstall("rm -- \"$registration.rpmnew\"\n" + mutation);
        var first = fixture.RunHelper();
        Assert.NotEqual(0, first.ExitCode);
        Assert.Contains("изменила содержимое или права", first.Error, StringComparison.Ordinal);
        Assert.Contains("600:", File.ReadAllText(Path.Combine(fixture.Pending, "registration-before")), StringComparison.Ordinal);
        Assert.False(File.Exists(registration + ".rpmnew"));
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);

        File.WriteAllText(registration, "airscan\n");
        File.SetUnixFileMode(registration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(fixture.Pending, recursive: true);
        Assert.Equal(0, fixture.RunHelper().ExitCode);
        Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
    }

    [Theory]
    [InlineData("600:99999:0")]
    [InlineData("600:0:99999")]
    public void ChangedOwnership_WithoutRpmnew_BlocksRetry(string metadata)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AirscanEnabled);
        var registration = Path.Combine(fixture.Root, "sane.d", "dll.d", "airscan");
        File.SetUnixFileMode(registration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Синтетическая подмена результата stat: смена UID/GID не требует root в CI.
        fixture.Stub("stat", "#!/usr/bin/env bash\nif [[ -f '" +
            Path.Combine(fixture.Root, "airscan-installed") +
            "' ]]; then printf '%s\\n' '" + metadata + "'; else exec /usr/bin/stat \"$@\"; fi\n");
        fixture.AfterAptInstall("rm -- \"$registration.rpmnew\"");
        var first = fixture.RunHelper();
        Assert.NotEqual(0, first.ExitCode);
        Assert.Contains("изменила содержимое или права", first.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(registration + ".rpmnew"));
        Assert.True(Directory.Exists(fixture.Pending));
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
    }

    [Fact]
    public void InterruptedAfterAptWithoutRpmnew_BlocksBeforeRetry()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.MissingAirscan);
        fixture.AfterAptInstall("kill -KILL \"$PPID\"");
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.True(Directory.Exists(fixture.Pending));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "airscan-installed")));
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.Equal(2, File.ReadAllLines(fixture.AptLog).Length);
    }

    [Fact]
    public void PendingWriteFailure_PreventsAptAndBlocksRetry()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.MissingAirscan);
        fixture.Stub("sync", "#!/bin/sh\nexit 96\n");
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.True(Directory.Exists(fixture.Pending));
        Assert.False(File.Exists(fixture.AptLog));
        File.Delete(Path.Combine(fixture.BinDirectory, "sync"));
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.False(File.Exists(fixture.AptLog));
    }

    [Fact]
    public void UnknownPendingObject_BlocksEvenWithInstalledPackage()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = RuntimeFixture.Create(RuntimeFixtureMode.AllCapabilitiesPresent);
        File.WriteAllText(fixture.Pending, "unknown");
        Assert.NotEqual(0, fixture.RunHelper().ExitCode);
        Assert.Equal("unknown", File.ReadAllText(fixture.Pending));
        Assert.False(File.Exists(fixture.AptLog));
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

    public enum RuntimeFixtureMode
    {
        AllCapabilitiesPresent,
        MissingIcuResolvable,
        MissingIcuUnresolvable,
        MissingAirscan,
        AirscanAptFailure,
        AirscanIncompleteInstall,
        AirscanBroken,
        AirscanEmpty,
        AirscanWrongArch,
        AirscanMissingDependency,
        AirscanUpdateFailure,
        AirscanMissingDiscover,
        AirscanMissingSymbols,
        MissingLoaderProbe,
        IncompleteLoaderProbe,
        MissingRunuser,
        IncompleteRunuser,
        AirscanDisabled,
        AirscanDisabledPartialFailure,
        AirscanDisabledExistingRpmnew,
        AirscanEnabled
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
        public string Pending => Path.Combine(Root, "sane.d.webassistant-airscan-pending");

        public void Stub(string name, string script) => WriteExecutable(Path.Combine(BinDirectory, name), script);

        public void AfterAptInstall(string script)
        {
            var apt = Path.Combine(BinDirectory, "apt-get");
            File.Move(apt, apt + "-original");
            Stub("apt-get", "#!/usr/bin/env bash\nset -euo pipefail\n\"$0-original\" \"$@\"\n" +
                "if [[ \"$1\" == install ]]; then\nregistration='" +
                Path.Combine(Root, "sane.d", "dll.d", "airscan") + "'\n" + script + "\nfi\n");
        }

        public static RuntimeFixture Create(RuntimeFixtureMode mode)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-alt-runtime-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var aptLog = Path.Combine(root, "apt.log");
            var installMarker = Path.Combine(root, "icu-installed");

            var airscanMarker = Path.Combine(root, "airscan-installed");
            var airscanLibrary = Path.Combine(root, "libsane-airscan.so.1");
            var airscanMissing = mode is RuntimeFixtureMode.MissingAirscan
                or RuntimeFixtureMode.AirscanAptFailure or RuntimeFixtureMode.AirscanIncompleteInstall
                or RuntimeFixtureMode.AirscanUpdateFailure or RuntimeFixtureMode.AirscanDisabled
                or RuntimeFixtureMode.AirscanDisabledPartialFailure or RuntimeFixtureMode.AirscanDisabledExistingRpmnew
                or RuntimeFixtureMode.AirscanEnabled;
            var saneRoot = Path.Combine(root, "sane.d");
            var dllDirectory = Directory.CreateDirectory(Path.Combine(saneRoot, "dll.d")).FullName;
            if (mode is RuntimeFixtureMode.AirscanDisabled or RuntimeFixtureMode.AirscanDisabledPartialFailure
                or RuntimeFixtureMode.AirscanDisabledExistingRpmnew or RuntimeFixtureMode.AirscanEnabled)
                File.WriteAllText(Path.Combine(dllDirectory, "airscan"),
                    mode == RuntimeFixtureMode.AirscanEnabled ? "airscan\n" : "# airscan\n");
            if (mode == RuntimeFixtureMode.AirscanDisabledExistingRpmnew)
                File.WriteAllText(Path.Combine(dllDirectory, "airscan.rpmnew"), "# existing\n");
            var loaderMarker = Path.Combine(root, "loader-installed");
            if (mode is RuntimeFixtureMode.MissingLoaderProbe or RuntimeFixtureMode.IncompleteLoaderProbe)
                WriteExecutable(Path.Combine(bin, "python3"), "#!/usr/bin/env bash\n" +
                    "[[ -f '" + loaderMarker + "' ]] || exit 1\nexec /usr/bin/python3 \"$@\"\n");
            var validLibrary = Path.Combine(root, "valid-airscan.so");
            var source = Path.Combine(root, "fixture.c");
            var symbols = new[] { "init", "exit", "get_devices", "open", "close",
                "get_option_descriptor", "control_option", "get_parameters", "start", "read",
                "cancel", "set_io_mode", "get_select_fd" };
            File.WriteAllText(source, "#include <stdio.h>\nint fixture(void) { return puts(\"fixture\"); }\n" +
                string.Join("\n", symbols.Select(name => "int sane_airscan_" + name + "(void) { return 0; }")));
            Compile("-shared", "-fPIC", "-o", validLibrary, source);
            if (!airscanMissing)
            {
                File.WriteAllText(airscanMarker, "");
                if (mode == RuntimeFixtureMode.AirscanEmpty)
                    File.WriteAllText(airscanLibrary, "");
                else if (mode == RuntimeFixtureMode.AirscanWrongArch)
                {
                    var bytes = File.ReadAllBytes(validLibrary);
                    bytes[18] = 0;
                    bytes[19] = 0;
                    File.WriteAllBytes(airscanLibrary, bytes);
                }
                else if (mode == RuntimeFixtureMode.AirscanMissingDependency)
                {
                    var dep = Path.Combine(root, "libfixturedep.so");
                    var dependent = Path.Combine(root, "dependent.c");
                    File.WriteAllText(dependent, "extern int fixture(void); int dependent(void) { return fixture(); }");
                    Compile("-shared", "-fPIC", "-o", dep, source);
                    Compile("-shared", "-fPIC", "-o", airscanLibrary, dependent,
                        "-L" + root, "-lfixturedep", "-Wl,-rpath,$ORIGIN");
                    File.Delete(dep);
                }
                else if (mode == RuntimeFixtureMode.AirscanMissingSymbols)
                {
                    File.WriteAllText(source, "#include <stdio.h>\nint fixture(void) { return puts(\"fixture\"); }");
                    Compile("-shared", "-fPIC", "-o", airscanLibrary, source);
                }
                else if (mode != RuntimeFixtureMode.AirscanBroken)
                    File.Copy(validLibrary, airscanLibrary);
            }
            if (mode is RuntimeFixtureMode.AirscanMissingDiscover or RuntimeFixtureMode.MissingRunuser or RuntimeFixtureMode.IncompleteRunuser)
            {
                foreach (var command in new[] { "bash", "grep", "cat", "ldd", "python3", "ln", "sed", "uname", "mkdir", "sync", "rm", "rmdir" })
                    File.CreateSymbolicLink(Path.Combine(bin, command), Path.Combine("/usr/bin", command));
                if (mode == RuntimeFixtureMode.AirscanMissingDiscover)
                    File.CreateSymbolicLink(Path.Combine(bin, "runuser"), "/usr/sbin/runuser");
                File.WriteAllText(Path.Combine(root, "isolated-path"), "");
            }
            if (mode != RuntimeFixtureMode.AirscanMissingDiscover)
                WriteExecutable(Path.Combine(bin, "airscan-discover"), "#!/usr/bin/env bash\nexit 99\n");
            WriteExecutable(Path.Combine(bin, "rpm"), string.Join("\n", new[]
            {
                "#!/usr/bin/env bash",
                "[[ \"$2\" == sane-airscan && -f '" + airscanMarker + "' ]] || exit 1",
                "case \"$1\" in",
                "  -q) exit 0 ;;",
                "  -ql) printf '%s\\n' '" + airscanLibrary + "' ;;",
                "  *) exit 90 ;;",
                "esac",
                ""
            }));

            WriteExecutable(Path.Combine(bin, "ldconfig"), mode switch
            {
                not (RuntimeFixtureMode.MissingIcuResolvable or RuntimeFixtureMode.MissingIcuUnresolvable) => """
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
  [[ '{{mode}}' != AirscanUpdateFailure ]] || exit 92
  exit 0
fi
if [[ "${1:-}" == 'install' && "$*" == *'libicu69'* ]]; then
  touch '{{installMarker}}'
  exit 0
fi
if [[ "$*" == 'install -y util-linux' ]]; then
  if [[ '{{mode}}' != IncompleteRunuser ]]; then
    ln -s /usr/sbin/runuser '{{bin}}/runuser'
  fi
  exit 0
fi
if [[ "$*" == 'install -y python3' ]]; then
  if [[ '{{mode}}' != IncompleteLoaderProbe ]]; then
    touch '{{loaderMarker}}'
  fi
  exit 0
fi
if [[ "$*" == 'install -y sane-airscan' ]]; then
  [[ '{{mode}}' != AirscanAptFailure ]] || exit 91
  if [[ -f '{{dllDirectory}}/airscan' ]]; then
    printf '%s\n' airscan > '{{dllDirectory}}/airscan.rpmnew'
  else
    printf '%s\n' airscan > '{{dllDirectory}}/airscan'
  fi
  [[ '{{mode}}' != AirscanDisabledPartialFailure ]] || exit 91
  touch '{{airscanMarker}}'
  if [[ '{{mode}}' != AirscanIncompleteInstall ]]; then
    cp '{{validLibrary}}' '{{airscanLibrary}}'
  fi
  exit 0
fi
exit 91
""");

            return new RuntimeFixture(root, bin, aptLog, installMarker);
        }

        private static void Compile(params string[] arguments)
        {
            var info = new ProcessStartInfo("cc") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить компилятор тестовой библиотеки.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }

        public ProcessResult RunLoader(string user = "") => Run("source \"$RUNTIME_HELPER\"; webassistant_airscan_loadable \"$FIXTURE_LIBRARY\" " + user);

        public ProcessResult RunHelper() => Run("source \"$RUNTIME_HELPER\"; ensure_webassistant_runtime_dependencies \"$FIXTURE_SANE_CONFIG\"");

        private ProcessResult Run(string command)
        {
            var helper = Path.Combine(FindRepositoryRoot(), "webassist", "install", "linux", "runtime-dependencies.sh");
            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
            startInfo.Environment["FIXTURE_LIBRARY"] = Path.Combine(Root, "libsane-airscan.so.1");
            startInfo.Environment["RUNTIME_HELPER"] = helper;
            startInfo.Environment["FIXTURE_SANE_CONFIG"] = Path.Combine(Root, "sane.d");
            startInfo.Environment["PATH"] = File.Exists(Path.Combine(Root, "isolated-path"))
                ? BinDirectory
                : BinDirectory + Path.PathSeparator + "/usr/bin:/bin:/usr/sbin:/sbin";

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
