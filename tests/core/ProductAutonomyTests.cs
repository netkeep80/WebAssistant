using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductAutonomyTests
{
    [Fact]
    public void ExportRoot_ContainsStandaloneAltOnlyGitLabPackagePipeline()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "webassist");
        var pipelinePath = Path.Combine(productRoot, ".gitlab-ci.yml");

        Assert.True(File.Exists(pipelinePath), "В export root отсутствует product-local .gitlab-ci.yml.");

        var pipeline = File.ReadAllText(pipelinePath);

        Assert.Contains("linux-package:", pipeline, StringComparison.Ordinal);
        Assert.Contains("./build/linux/package.sh", pipeline, StringComparison.Ordinal);
        Assert.Contains("artifacts/linux-x64/", pipeline, StringComparison.Ordinal);

        Assert.DoesNotContain("windows-package:", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WEBASSISTANT_WINDOWS_RUNNER_TAG", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("build\\windows\\package.bat", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artifacts/windows-x64/", pipeline, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("NAPS2_RPM_URL", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--naps2-rpm", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".rpm", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("include:", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("../", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("..\\", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRoot_ReadmeDocumentsCanonicalDistributionAndAltOnlyGitLabBoundary()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "webassist", "README.md"));

        Assert.Contains("WebAssistant-win-x64-<VERSION>.exe", readme, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-linux-x64-<VERSION>.zip", readme, StringComparison.Ordinal);
        Assert.Contains(".gitlab-ci.yml", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build/linux/package.sh", readme, StringComparison.Ordinal);

        Assert.DoesNotContain("WEBASSISTANT_WINDOWS_RUNNER_TAG", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows job", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("install.bat\n```", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductDocs_DescribePackageOwnedConfigAndCanonicalInstalledLifecycle()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "webassist");
        var readme = File.ReadAllText(Path.Combine(productRoot, "README.md"));
        var windows = File.ReadAllText(Path.Combine(productRoot, "docs", "windows-service.md"));
        var linux = File.ReadAllText(Path.Combine(productRoot, "docs", "linux-service.md"));

        Assert.Contains("package-time", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebAssistant-win-x64-<VERSION>.exe", windows, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-linux-x64-<VERSION>.zip", linux, StringComparison.Ordinal);
        Assert.Contains("ALT Linux 10.1", linux, StringComparison.Ordinal);
        Assert.Contains("/var/log/webassistant", linux, StringComparison.Ordinal);
        Assert.Contains("/var/lib/webassistant", linux, StringComparison.Ordinal);

        Assert.DoesNotContain("installer создаёт default", windows, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installer записывает JSON-конфигурацию", linux, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("устанавливает default appsettings", windows, StringComparison.OrdinalIgnoreCase);
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
