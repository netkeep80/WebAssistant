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
}
