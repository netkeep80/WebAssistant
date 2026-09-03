using Xunit;

namespace WebAssistant.CoreTests;

public sealed class VirtualScannerWorkflowTests
{
    [Fact]
    public void VirtualScannerWorkflow_ExercisesDirectProductAdaptersWithoutLegacyCliPath()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "virtual-scanner.yml");
        var linuxHarness = Path.Combine(root, "tests", "virtual-scanner", "linux", "run-sane-test.sh");
        var windowsHarness = Path.Combine(root, "tests", "virtual-scanner", "windows", "install-twain-sample.ps1");

        Assert.True(File.Exists(workflowPath), "virtual-scanner workflow is missing");
        Assert.True(File.Exists(linuxHarness), "Linux SANE harness is missing");
        Assert.True(File.Exists(windowsHarness), "Windows TWAIN sample installer is missing");

        var workflow = File.ReadAllText(workflowPath);
        Assert.Contains("WEBASSISTANT_LINUX_VIRTUAL", workflow);
        Assert.Contains("WEBASSISTANT_WINDOWS_VIRTUAL", workflow);
        Assert.Contains("WEBASSISTANT_PLATFORM_E2E", workflow);
        Assert.Contains("Category=LinuxVirtualScanner", workflow);
        Assert.Contains("Category=WindowsVirtualScanner", workflow);
        Assert.Contains("Category=PlatformVirtualEndToEnd", workflow);
        Assert.Contains("webassist/vendor/nuget", workflow);
        Assert.False(workflow.Contains("run-naps2-cli-test", StringComparison.OrdinalIgnoreCase));
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
}
