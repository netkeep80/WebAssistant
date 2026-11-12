using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductAutonomyTests
{
    [Fact]
    public void ExportRoot_ContainsStandaloneAltOnlyGitLabBuildAndStorageBootstrap()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "webassist");
        var pipelinePath = Path.Combine(productRoot, ".gitlab-ci.yml");

        Assert.True(File.Exists(pipelinePath), "В export root отсутствует product-local .gitlab-ci.yml.");

        var pipeline = File.ReadAllText(pipelinePath);

        // Storage initialization is manual; the build path is inherited from GitLab group CI.
        Assert.Contains("utility-tasks", pipeline, StringComparison.Ordinal);
        Assert.Contains("prepare-release-storage:", pipeline, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/publish-ssh.sh --init-storage \"$CI_PROJECT_NAME\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("when: manual", pipeline, StringComparison.Ordinal);
        Assert.Contains("CI_PROJECT_NAME", pipeline, StringComparison.Ordinal);
        Assert.Contains("/webassistant-builder:${WEBASSISTANT_BUILDER_TAG}", pipeline, StringComparison.Ordinal);

        // One canonical Linux packager, with downstream-local source revision.
        Assert.Contains("build-altlinux-release:", pipeline, StringComparison.Ordinal);
        Assert.Contains("WEBASSISTANT_SOURCE_SHA=\"$CI_COMMIT_SHA\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0", pipeline, StringComparison.Ordinal);
        Assert.Contains("bash build/linux/package.sh artifacts/linux-x64", pipeline, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/release.sh assemble", pipeline, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/release.sh verify", pipeline, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/publish-ssh.sh", pipeline, StringComparison.Ordinal);
        const string preflight = "bash build/distribution/publish-ssh.sh --check-ci-environment";
        Assert.Contains(preflight, pipeline, StringComparison.Ordinal);
        Assert.True(
            pipeline.IndexOf(preflight, StringComparison.Ordinal) <
            pipeline.IndexOf("dotnet --list-sdks", StringComparison.Ordinal),
            "Проверка CI-переменных должна выполняться до проверки SDK.");
        Assert.True(
            pipeline.IndexOf(preflight, StringComparison.Ordinal) <
            pipeline.IndexOf("bash tests/distribution-release.test.sh", StringComparison.Ordinal),
            "Проверка CI-переменных должна выполняться до тестов и упаковки.");
        Assert.Contains("null|masked_null|", pipeline, StringComparison.Ordinal);
        Assert.Contains("tests/distribution-release.test.sh", pipeline, StringComparison.Ordinal);
        Assert.Contains("$CI_DEFAULT_BRANCH", pipeline, StringComparison.Ordinal);

        var sshPublisher = File.ReadAllText(Path.Combine(productRoot, "build", "distribution", "publish-ssh.sh"));
        Assert.Contains("CI_BUILD_PROJECT_PATH", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_HOST", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_USER", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA_SECRET", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=accept-new", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("SSH_ASKPASS_REQUIRE=force", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("--init-storage", sshPublisher, StringComparison.Ordinal);

        var storagePublisher = File.ReadAllText(Path.Combine(productRoot, "build", "distribution", "remote-publish.sh"));
        Assert.Contains("cp -a -- \"$stage/.\" \"$final/\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("diff -qr -- \"$stage\" \"$final\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("sudo -n install -d", storagePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("mv -T", storagePublisher, StringComparison.Ordinal);

        foreach (var relativePath in new[]
        {
            "build/distribution/release.sh",
            "build/distribution/remote-publish.sh",
            "build/distribution/publish-ssh.sh",
            "tests/distribution-release.test.sh"
        })
        {
            Assert.True(File.Exists(Path.Combine(productRoot, relativePath)),
                $"Отсутствует автономный сценарий export root: {relativePath}");
        }

        // Installer storage is externally authoritative, never a GitLab Job Artifact.
        Assert.DoesNotContain("  artifacts:", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("expire_in:", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netkeep80/WebAssistant", pipeline, StringComparison.OrdinalIgnoreCase);

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
        Assert.Contains("CI_GITLAB_API_ACCESS_TOKEN", readme, StringComparison.Ordinal);
        Assert.Contains("webassistant-builder", readme, StringComparison.Ordinal);
        Assert.Contains("CI_BUILD_PROJECT_PATH", readme, StringComparison.Ordinal);
        Assert.Contains("build-altlinux-release", readme, StringComparison.Ordinal);
        Assert.Contains("prepare-release-storage", readme, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA", readme, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA_SECRET", readme, StringComparison.Ordinal);
        Assert.Contains("copy → verify → delete", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductDocs_DescribePackageOwnedConfigAndCanonicalInstalledLifecycle()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "webassist");
        var readme = File.ReadAllText(Path.Combine(productRoot, "README.md"));
        var windows = File.ReadAllText(Path.Combine(productRoot, "docs", "windows-service.md"));
        var linux = File.ReadAllText(Path.Combine(productRoot, "docs", "linux-service.md"));

        Assert.Contains("%ProgramData%\\WebAssistant\\config\\appsettings.json", readme, StringComparison.Ordinal);
        Assert.Contains("appsettings.example.json", readme, StringComparison.Ordinal);
        Assert.Contains("persistent", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("После формирования ZIP `appsettings.json` является неизменяемой частью пакета.", linux, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-win-x64-<VERSION>.exe", windows, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-linux-x64-<VERSION>.zip", linux, StringComparison.Ordinal);
        Assert.Contains("ALT Linux 10.1", linux, StringComparison.Ordinal);
        Assert.Contains("/var/log/webassistant", linux, StringComparison.Ordinal);
        Assert.Contains("/var/lib/webassistant", linux, StringComparison.Ordinal);

        Assert.DoesNotContain("installer создаёт default", windows, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installer записывает JSON-конфигурацию", linux, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("устанавливает default appsettings", windows, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportRoot_DistributionShellReleaseTest_PassesWithNonExecutableScripts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var testScript = Path.Combine(FindRepositoryRoot(), "webassist", "tests", "distribution-release.test.sh");
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(testScript);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Не удалось запустить тест публикации релиза.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0,
            $"Сценарий публикации завершился ошибкой. stdout: {output}\\nstderr: {errors}");
        Assert.Contains("distribution release assembly, verification, SSH transfer, idempotence, tamper rejection: PASS",
            output, StringComparison.Ordinal);
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
