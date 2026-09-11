using WebAssistant.UpgradePreflight;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsUpgradeServiceIdentityTests
{
    [Fact]
    public void CreateServiceProcessIdentity_UsesConfiguredScmExecutablePath()
    {
        var configuredPath = @"C:\Program Files\WebAssistant\WebAssistant.exe";
        var started = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        var identity = WindowsUpgradeEnvironment.CreateServiceProcessIdentity(
            processId: 1308,
            parentProcessId: 777,
            configuredExecutablePath: configuredPath,
            startTimeUtc: started);

        Assert.Equal(1308, identity.ProcessId);
        Assert.Equal(777, identity.ParentProcessId);
        Assert.Equal(configuredPath, identity.ImagePath);
        Assert.Equal(started, identity.StartTimeUtc);
    }

    [Fact]
    public void MatchesOpenProcessInstance_UsesCapturedCreationTimeAsPidReuseGuard()
    {
        var started = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var expected = new ProcessIdentity(
            ProcessId: 1308,
            ParentProcessId: 777,
            ImagePath: @"C:\Program Files\WebAssistant\WebAssistant.exe",
            StartTimeUtc: started);

        Assert.True(WindowsUpgradeEnvironment.MatchesOpenProcessInstance(expected, started));
        Assert.False(WindowsUpgradeEnvironment.MatchesOpenProcessInstance(
            expected,
            started.AddTicks(1)));
    }

    [Fact]
    public void RetainedServiceProcess_DoesNotRequeryImagePathAfterScmIdentityCapture()
    {
        var source = ReadRequired(
            "webassist/build/windows/upgrade-preflight/RetainedServiceProcessUpgradeEnvironment.cs");

        Assert.DoesNotContain("QueryFullProcessImageName", source, StringComparison.Ordinal);
    }

    private static string ReadRequired(string relativePath)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
