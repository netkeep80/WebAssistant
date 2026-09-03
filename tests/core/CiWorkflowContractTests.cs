using Xunit;

namespace WebAssistant.CoreTests;

public sealed class CiWorkflowContractTests
{
    [Fact]
    public void ProductCi_DefinesStableFailClosedGateAndReusableComponents()
    {
        var root = FindRepositoryRoot();
        var workflows = Path.Combine(root, ".github", "workflows");
        var ci = File.ReadAllText(Path.Combine(workflows, "ci.yml"));

        Assert.Contains("concurrency:", ci, StringComparison.Ordinal);
        Assert.Contains("group: webassistant-pr-${{ github.event.pull_request.number }}", ci, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: true", ci, StringComparison.Ordinal);
        Assert.Contains("ci-required:", ci, StringComparison.Ordinal);
        Assert.Contains("name: ci-required", ci, StringComparison.Ordinal);
        Assert.Contains("if: ${{ always() }}", ci, StringComparison.Ordinal);
        Assert.Contains("true:success|false:success|false:skipped", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case true skipped fail", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case true failure fail", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case true cancelled fail", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case false skipped pass", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case false failure fail", ci, StringComparison.Ordinal);
        Assert.Contains("assert_case false cancelled fail", ci, StringComparison.Ordinal);
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
