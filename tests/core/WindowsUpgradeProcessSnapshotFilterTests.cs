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
}
