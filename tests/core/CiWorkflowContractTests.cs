using Xunit;

namespace WebAssistant.CoreTests;

public sealed class CiWorkflowContractTests
{
    [Fact]
    public void ProductCiCaller_DefinesStableFailClosedGateAndReusableComponents()
    {
        var root = FindRepositoryRoot();
        var workflows = Path.Combine(root, ".github", "workflows");
        var ciPath = Path.Combine(workflows, "ci.yml");
        var evaluatorPath = Path.Combine(root, ".github", "scripts", "ci-required.sh");

        Assert.True(File.Exists(ciPath), "Отсутствует единый PR caller .github/workflows/ci.yml.");
        Assert.True(File.Exists(evaluatorPath), "Отсутствует repository-owned ci-required evaluator.");

        var ci = File.ReadAllText(ciPath);
        Assert.Contains("ci-required:", ci, StringComparison.Ordinal);
        Assert.Contains("name: ci-required", ci, StringComparison.Ordinal);
        Assert.Contains("if: ${{ always() }}", ci, StringComparison.Ordinal);
        Assert.Contains("requirements", ci, StringComparison.Ordinal);
        Assert.Contains("core", ci, StringComparison.Ordinal);
        Assert.Contains("linux-systemd", ci, StringComparison.Ordinal);
        Assert.Contains("windows-service", ci, StringComparison.Ordinal);
        Assert.Contains("virtual-scanner", ci, StringComparison.Ordinal);
        Assert.Contains(".github/scripts/ci-required.sh", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("repo-guard.yml", ci, StringComparison.Ordinal);

        AssertReusableComponent(workflows, "core.yml");
        AssertReusableComponent(workflows, "linux-systemd.yml");
        AssertReusableComponent(workflows, "windows-service.yml");
        AssertReusableComponent(workflows, "virtual-scanner.yml");

        var repoGuard = File.ReadAllText(Path.Combine(workflows, "repo-guard.yml"));
        Assert.Contains("name: repo-guard", repoGuard, StringComparison.Ordinal);
        Assert.Contains("pull_request:", repoGuard, StringComparison.Ordinal);
    }

    private static void AssertReusableComponent(string workflows, string fileName)
    {
        var text = File.ReadAllText(Path.Combine(workflows, fileName));

        Assert.Contains("workflow_call:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", text, StringComparison.Ordinal);
        Assert.Contains("push:", text, StringComparison.Ordinal);
        Assert.Contains("main", text, StringComparison.Ordinal);
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
