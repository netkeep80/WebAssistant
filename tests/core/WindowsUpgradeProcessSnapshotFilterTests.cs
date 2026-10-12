using WebAssistant.UpgradePreflight;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsUpgradeProcessSnapshotFilterTests
{
    [Theory]
    [InlineData("System", false)]
    [InlineData("WebAssistant.exe", false)]
    [InlineData("NAPS2.Worker.exe", true)]
    [InlineData("naps2.worker.exe", true)]
    public void WorkerSnapshotCandidate_FiltersBeforeOpeningProcess(string executableName, bool expected)
    {
        Assert.Equal(expected, WindowsUpgradeEnvironment.IsWorkerSnapshotCandidate(executableName));
    }
    [Theory]
    [InlineData(true, 5, 0x00000000u, true)]
    [InlineData(true, 5, 0x00000102u, false)]
    [InlineData(true, 5, 0xFFFFFFFFu, false)]
    [InlineData(true, 6, 0x00000000u, false)]
    [InlineData(false, 5, 0x00000000u, false)]
    public void InaccessibleWorkerImage_CanBeSkippedOnlyIfProcessAlreadyExited(
        bool allowInaccessible,
        int win32Error,
        uint processWaitResult,
        bool expected)
    {
        Assert.Equal(expected, WindowsUpgradeEnvironment.CanSkipWorkerImageError(
            allowInaccessible, win32Error, processWaitResult));
    }

}
