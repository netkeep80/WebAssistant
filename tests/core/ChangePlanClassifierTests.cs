using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ChangePlanClassifierTests
{
    [Fact]
    public void DocsOnly_RequiresCoreWithoutPlatformSuites()
    {
        AssertPlan(
            ["README.md", "webassist/docs/api.md", "webassist/VERSION"],
            core: true,
            linuxSystemd: false,
            windowsService: false,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: false,
            virtualWindows: false,
            smokeLinux: false,
            smokeWindows: false,
            fullCrossPlatform: false);
    }

    [Fact]
    public void WindowsScannerChange_RequiresWindowsPlatformWithoutDistributionBuild()
    {
        AssertPlan(
            ["webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs", "webassist/VERSION"],
            core: true,
            linuxSystemd: false,
            windowsService: true,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: false,
            virtualWindows: true,
            smokeLinux: false,
            smokeWindows: true,
            fullCrossPlatform: false);
    }

    [Fact]
    public void LinuxScannerChange_RequiresLinuxPlatformWithoutDistributionBuild()
    {
        AssertPlan(
            ["webassist/src/WebAssistant/Scanning/LinuxScanAdapter.cs", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: false,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: true,
            virtualWindows: false,
            smokeLinux: true,
            smokeWindows: false,
            fullCrossPlatform: false);
    }

    [Fact]
    public void WindowsDistributionChange_RequiresSharedWindowsInstaller()
    {
        AssertPlan(
            ["webassist/build/windows/package.ps1", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: false,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void LinuxDistributionChange_RequiresSharedLinuxInstaller()
    {
        AssertPlan(
            ["webassist/build/linux/package.sh", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: false,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void SharedDistributionChange_RequiresBothSharedInstallers()
    {
        AssertPlan(
            ["webassist/build/common/write-provenance.sh", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);

        AssertPlan(
            [".github/workflows/build-installers.yml", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);

        AssertPlan(
            [".github/workflows/installer-acceptance.yml", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void CommonProduct_RequiresFullCrossPlatformEvidenceWithoutDistributionBuild()
    {
        AssertPlan(
            ["webassist/src/WebAssistant/Http/ScanCoordinator.cs", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void MixedDistributionChanges_RequireBothSharedInstallers()
    {
        AssertPlan(
            [
                "webassist/build/windows/package.ps1",
                "webassist/build/linux/package.sh",
                "webassist/VERSION"
            ],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void WindowsLifecycleWorkflowChange_RequiresOnlyWindowsPlatformEvidence()
    {
        AssertPlan(
            [".github/workflows/windows-service.yml", "webassist/VERSION"],
            core: true,
            linuxSystemd: false,
            windowsService: true,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: false,
            virtualWindows: true,
            smokeLinux: false,
            smokeWindows: true,
            fullCrossPlatform: false);
    }

    [Fact]
    public void LinuxLifecycleWorkflowChange_RequiresOnlyLinuxPlatformEvidence()
    {
        AssertPlan(
            [".github/workflows/linux-systemd.yml", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: false,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: true,
            virtualWindows: false,
            smokeLinux: true,
            smokeWindows: false,
            fullCrossPlatform: false);
    }

    [Fact]
    public void UnknownProductPath_FailsClosedWithoutInventingDistributionScope()
    {
        AssertPlan(
            ["webassist/src/WebAssistant/NewCapability/Thing.cs", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: false,
            installerWindows: false,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void ClassifierOrTopLevelCiChange_RequiresBothSharedInstallers()
    {
        AssertPlan(
            ["ci/change-plan.sh", ".github/workflows/ci.yml", "webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    [Fact]
    public void VersionOnlyChange_FailsClosedToBothSharedInstallers()
    {
        AssertPlan(
            ["webassist/VERSION"],
            core: true,
            linuxSystemd: true,
            windowsService: true,
            installerLinux: true,
            installerWindows: true,
            virtualLinux: true,
            virtualWindows: true,
            smokeLinux: true,
            smokeWindows: true,
            fullCrossPlatform: true);
    }

    private static void AssertPlan(
        string[] paths,
        bool core,
        bool linuxSystemd,
        bool windowsService,
        bool installerLinux,
        bool installerWindows,
        bool virtualLinux,
        bool virtualWindows,
        bool smokeLinux,
        bool smokeWindows,
        bool fullCrossPlatform)
    {
        var plan = RunPlan(paths);

        Assert.Equal(core, Flag(plan, "core"));
        Assert.Equal(linuxSystemd, Flag(plan, "linux_systemd"));
        Assert.Equal(windowsService, Flag(plan, "windows_service"));
        Assert.Equal(installerLinux, Flag(plan, "installer_linux"));
        Assert.Equal(installerWindows, Flag(plan, "installer_windows"));
        Assert.Equal(virtualLinux, Flag(plan, "virtual_linux"));
        Assert.Equal(virtualWindows, Flag(plan, "virtual_windows"));
        Assert.Equal(smokeLinux, Flag(plan, "smoke_linux"));
        Assert.Equal(smokeWindows, Flag(plan, "smoke_windows"));
        Assert.Equal(virtualLinux || virtualWindows, Flag(plan, "virtual_scanner"));
        Assert.Equal(fullCrossPlatform, Flag(plan, "full_cross_platform"));
    }

    private static IReadOnlyDictionary<string, string> RunPlan(IEnumerable<string> paths)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "ci", "change-plan.sh");
        Assert.True(File.Exists(script), $"Не найден repository-owned classifier: {script}");

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--paths");
        foreach (var path in paths)
        {
            startInfo.ArgumentList.Add(path);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить classifier.");
        Assert.True(process.WaitForExit(10_000), "Classifier не завершился за 10 секунд.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"Classifier exit={process.ExitCode}: {stderr}");

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static bool Flag(IReadOnlyDictionary<string, string> plan, string key)
    {
        Assert.True(plan.TryGetValue(key, out var value), $"Classifier не вернул output {key}.");
        Assert.True(value is "true" or "false", $"Classifier вернул недопустимое значение {key}={value}.");
        return value == "true";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                Directory.Exists(Path.Combine(directory.FullName, ".github")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория.");
    }
}
