using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductAutonomyTests
{
    [Fact]
    public void ExportRoot_ContainsStandaloneGitLabPackagePipeline()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "webassist");
        var pipelinePath = Path.Combine(productRoot, ".gitlab-ci.yml");

        Assert.True(File.Exists(pipelinePath), "В export root отсутствует product-local .gitlab-ci.yml.");

        var pipeline = File.ReadAllText(pipelinePath);

        Assert.Contains("WEBASSISTANT_WINDOWS_RUNNER_TAG", pipeline, StringComparison.Ordinal);
        Assert.Contains("build\\windows\\package.bat", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("./build/linux/package.sh", pipeline, StringComparison.Ordinal);
        Assert.Contains("artifacts/windows-x64/", pipeline, StringComparison.Ordinal);
        Assert.Contains("artifacts/linux-x64/", pipeline, StringComparison.Ordinal);

        Assert.DoesNotContain("NAPS2_RPM_URL", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--naps2-rpm", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".rpm", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("include:", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("../", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("..\\", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRoot_ReadmeDocumentsStandaloneGitLabBuildInputs()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "webassist", "README.md"));

        Assert.Contains(".gitlab-ci.yml", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WEBASSISTANT_WINDOWS_RUNNER_TAG", readme, StringComparison.Ordinal);
        Assert.Contains("build/linux/package.sh", readme, StringComparison.Ordinal);
        Assert.Contains("build\\windows\\package.bat", readme, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
