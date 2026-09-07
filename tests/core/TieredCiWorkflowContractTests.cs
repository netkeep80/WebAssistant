using Xunit;

namespace WebAssistant.CoreTests;

public sealed class TieredCiWorkflowContractTests
{
    [Fact]
    public void TopLevelCi_DefinesTieredEventsAndAggregators()
    {
        var root = FindRepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
        Assert.Contains("pr_number:", ci, StringComparison.Ordinal);
        Assert.Contains("pull-requests: read", ci, StringComparison.Ordinal);
        Assert.Contains("final_required:", ci, StringComparison.Ordinal);
        Assert.Contains("smoke_linux:", ci, StringComparison.Ordinal);
        Assert.Contains("smoke_windows:", ci, StringComparison.Ordinal);
        Assert.Contains("scanner-smoke:", ci, StringComparison.Ordinal);
        Assert.Contains("mode: smoke", ci, StringComparison.Ordinal);
        Assert.Contains("scanner-final:", ci, StringComparison.Ordinal);
        Assert.Contains("mode: full", ci, StringComparison.Ordinal);
        Assert.Contains("ci-fast:", ci, StringComparison.Ordinal);
        Assert.Contains("name: ci-fast", ci, StringComparison.Ordinal);
        Assert.Contains("GITHUB_SHA", ci, StringComparison.Ordinal);
        Assert.Contains("gh api", ci, StringComparison.Ordinal);
        Assert.Contains("pulls/$pr_number", ci, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'workflow_dispatch'", ci, StringComparison.Ordinal);
        Assert.Contains("github.event.pull_request.draft == false", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusableWorkflows_CheckoutExactSourceRef()
    {
        var root = FindRepositoryRoot();
        var workflows = Path.Combine(root, ".github", "workflows");
        foreach (var fileName in new[] { "core.yml", "linux-systemd.yml", "windows-service.yml" })
        {
            var text = File.ReadAllText(Path.Combine(workflows, fileName));
            Assert.Contains("source_ref:", text, StringComparison.Ordinal);
            Assert.Contains("inputs.source_ref", text, StringComparison.Ordinal);
            Assert.Contains("github.sha", text, StringComparison.Ordinal);
            Assert.Contains("ref:", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VirtualScanner_DefinesFailClosedSmokeAndFullModes()
    {
        var root = FindRepositoryRoot();
        var scanner = File.ReadAllText(Path.Combine(root, ".github", "workflows", "virtual-scanner.yml"));

        Assert.Contains("resolve-inputs:", scanner, StringComparison.Ordinal);
        Assert.Contains("source_ref:", scanner, StringComparison.Ordinal);
        Assert.Contains("mode:", scanner, StringComparison.Ordinal);
        Assert.Contains("smoke", scanner, StringComparison.Ordinal);
        Assert.Contains("full", scanner, StringComparison.Ordinal);
        Assert.Contains("Category=LinuxVirtualScanner", scanner, StringComparison.Ordinal);
        Assert.Contains("Category=WindowsVirtualScanner", scanner, StringComparison.Ordinal);
        Assert.Contains("Category=PlatformVirtualEndToEnd", scanner, StringComparison.Ordinal);
        Assert.Contains("needs.resolve-inputs.outputs.mode == 'full'", scanner, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelCi_PassesResolvedExactHeadToChildren()
    {
        var root = FindRepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("base_sha:", ci, StringComparison.Ordinal);
        Assert.Contains("head_sha:", ci, StringComparison.Ordinal);
        Assert.Contains("source_ref: ${{ needs.requirements.outputs.head_sha }}", ci, StringComparison.Ordinal);
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
