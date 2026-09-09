using Xunit;

namespace WebAssistant.CoreTests;

public sealed class IsolatedExportRootAcceptanceTests
{
    [Fact]
    public void WindowsServiceWorkflow_BuildsFromCopiedExportRoot()
    {
        var workflow = ReadRequired(".github/workflows/windows-service.yml");
        var harness = ReadRequired("tests/windows-service/run-service-acceptance.ps1");

        Assert.Contains("WEBASSISTANT_EXPORT_ROOT", workflow, StringComparison.Ordinal);
        Assert.Contains("Join-Path $env:GITHUB_WORKSPACE 'webassist'", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $exportRoot -Recurse -Force", workflow, StringComparison.Ordinal);
        Assert.Contains("WebAssistant.sln", workflow, StringComparison.Ordinal);
        Assert.Contains(".gitlab-ci.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("README.md", workflow, StringComparison.Ordinal);
        Assert.Contains("build/windows/package.bat", workflow, StringComparison.Ordinal);
        Assert.Contains("-ProductRoot $env:WEBASSISTANT_EXPORT_ROOT", workflow, StringComparison.Ordinal);

        Assert.Contains("[string]$ProductRoot", harness, StringComparison.Ordinal);
        Assert.Contains("$packageBatch = Join-Path $ProductRoot \"build/windows/package.bat\"", harness, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSystemdWorkflow_BuildsOnceFromCopiedExportRootAndConsumesArtifact()
    {
        var workflow = ReadRequired(".github/workflows/linux-systemd.yml");
        var lifecycle = ReadRequired("tests/linux-systemd/run-systemd-acceptance.sh");
        var consumer = ReadRequired("tests/linux-systemd/run-installer-acceptance.sh");

        Assert.Contains("WEBASSISTANT_EXPORT_ROOT", workflow, StringComparison.Ordinal);
        Assert.Contains("cp -a \"$GITHUB_WORKSPACE/webassist/.\" \"$export_root/\"", workflow, StringComparison.Ordinal);
        Assert.Contains("WebAssistant.sln", workflow, StringComparison.Ordinal);
        Assert.Contains(".gitlab-ci.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("README.md", workflow, StringComparison.Ordinal);
        Assert.Contains("$WEBASSISTANT_EXPORT_ROOT/build/linux/package.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/download-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("run-installer-acceptance.sh", workflow, StringComparison.Ordinal);

        Assert.DoesNotContain("PRODUCT_ROOT=", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("package_script=", lifecycle, StringComparison.Ordinal);
        Assert.Contains("run-systemd-acceptance.sh", consumer, StringComparison.Ordinal);
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
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
