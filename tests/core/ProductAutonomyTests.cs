using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductAutonomyTests
{
    [Fact]
    public void ExportRoot_SeparatesPlatformBuildsFromPlatformNeutralVersionPublication()
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

        // Платформенные задачи собирают и публикуют байты; общий VERSION публикуется позже на платформенно-нейтральной стадии.
        var stages = GetTopLevelYamlBlock(pipeline, "stages")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToArray();
        Assert.Equal(new[] { "utility-tasks", "build", "publish" }, stages);

        var buildJob = GetTopLevelYamlBlock(pipeline, "build-altlinux-release");
        var publishJob = GetTopLevelYamlBlock(pipeline, "publish-release-version");

        Assert.Contains("stage: build", buildJob, StringComparison.Ordinal);
        Assert.Contains("stage: publish", publishJob, StringComparison.Ordinal);
        Assert.Contains("if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH'", buildJob, StringComparison.Ordinal);
        Assert.Contains("if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH'", publishJob, StringComparison.Ordinal);
        Assert.Contains("when: on_success", buildJob, StringComparison.Ordinal);
        Assert.Contains("when: on_success", publishJob, StringComparison.Ordinal);
        Assert.DoesNotContain("needs:", publishJob, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_failure:", buildJob, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_failure:", publishJob, StringComparison.Ordinal);
        Assert.DoesNotContain("when: manual", buildJob, StringComparison.Ordinal);
        Assert.DoesNotContain("when: manual", publishJob, StringComparison.Ordinal);

        Assert.Contains("WEBASSISTANT_SOURCE_SHA=\"$CI_COMMIT_SHA\"", buildJob, StringComparison.Ordinal);
        Assert.Contains("WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0", buildJob, StringComparison.Ordinal);
        Assert.Contains("bash build/linux/package.sh artifacts/linux-x64", buildJob, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/release.sh assemble", buildJob, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/release.sh verify", buildJob, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/publish-ssh.sh", buildJob, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-config-version.sh", buildJob, StringComparison.Ordinal);

        Assert.Contains("bash build/distribution/publish-config-version.sh --check-ci-environment", publishJob, StringComparison.Ordinal);
        Assert.Contains("bash build/distribution/publish-config-version.sh \"$CI_PROJECT_NAME\" \"$version\"", publishJob, StringComparison.Ordinal);
        foreach (var platformSpecific in new[]
        {
            "altlinux", "linux-x64", "windows", "installer", "manifest.json",
            "publish-ssh.sh", "release.sh", "CI_BUILD_PROJECT_PATH", "CI_SSH_"
        })
        {
            Assert.DoesNotContain(platformSpecific, publishJob, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("create-release-config-variable:", pipeline, StringComparison.Ordinal);
        var createConfigVariableJob = GetTopLevelYamlBlock(pipeline, "create-release-config-variable");
        Assert.Contains("CI_RELEASE_CONFIG_REPO", createConfigVariableJob, StringComparison.Ordinal);
        Assert.Contains("CI_GITLAB_API_ACCESS_TOKEN", createConfigVariableJob, StringComparison.Ordinal);
        Assert.Contains("PRIVATE-TOKEN: $CI_GITLAB_API_ACCESS_TOKEN", createConfigVariableJob, StringComparison.Ordinal);
        Assert.DoesNotContain("CI_PROJECT_ACCESS_TOKEN", createConfigVariableJob, StringComparison.Ordinal);
        Assert.DoesNotContain("bash tests/distribution-release.test.sh", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("bash tests/promotion-version.test.sh", pipeline, StringComparison.Ordinal);
        const string preflight = "bash build/distribution/publish-ssh.sh --check-ci-environment";
        Assert.Contains(preflight, pipeline, StringComparison.Ordinal);
        Assert.True(
            pipeline.IndexOf(preflight, StringComparison.Ordinal) <
            pipeline.IndexOf("dotnet --list-sdks", StringComparison.Ordinal),
            "Проверка CI-переменных должна выполняться до проверки SDK.");
        Assert.True(
            pipeline.IndexOf(preflight, StringComparison.Ordinal) <
            pipeline.IndexOf("bash build/linux/package.sh", StringComparison.Ordinal),
            "Проверка CI-переменных должна выполняться до упаковки.");
        Assert.Contains("null|masked_null|", pipeline, StringComparison.Ordinal);
        Assert.Contains("$CI_DEFAULT_BRANCH", pipeline, StringComparison.Ordinal);

        var sshPublisher = File.ReadAllText(Path.Combine(productRoot, "build", "distribution", "publish-ssh.sh"));
        Assert.Contains("CI_BUILD_PROJECT_PATH", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("stage_full=\"${CI_BUILD_PROJECT_PATH}/$product/releases/$stage\"", sshPublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("stage_full=\"${CI_BUILD_PROJECT_PATH}/products/", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_HOST", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_USER", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA_SECRET", sshPublisher, StringComparison.Ordinal);
        var configPublisher = File.ReadAllText(Path.Combine(productRoot, "build", "distribution", "publish-config-version.sh"));
        Assert.Contains("CI_GITLAB_CICD_AGENT_USER", configPublisher, StringComparison.Ordinal);
        Assert.Contains("*Username*) printf '%s' \"$CI_GITLAB_CICD_AGENT_USER\"", configPublisher, StringComparison.Ordinal);
        Assert.Contains("*Password*) printf '%s' \"$CI_GITLAB_CICD_AGENT_SECRET\"", configPublisher, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=accept-new", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("SSH_ASKPASS_REQUIRE=force", sshPublisher, StringComparison.Ordinal);
        Assert.Contains("--init-storage", sshPublisher, StringComparison.Ordinal);

        var storagePublisher = File.ReadAllText(Path.Combine(productRoot, "build", "distribution", "remote-publish.sh"));
        Assert.Contains("cp -a -- \"$stage/.\" \"$final/\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("diff -qr -- \"$stage\" \"$final\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("sudo -n install -d", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("project_dir=\"$root/$product\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("parent=\"$root/$product/releases\"", storagePublisher, StringComparison.Ordinal);
        Assert.Contains("project storage directory missing or not writable; run prepare-release-storage", storagePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("parent=\"$root/products/", storagePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("mv -T", storagePublisher, StringComparison.Ordinal);

        foreach (var relativePath in new[]
        {
            "build/distribution/release.sh",
            "build/distribution/remote-publish.sh",
            "build/distribution/publish-ssh.sh",
            "build/distribution/publish-config-version.sh",
            "build/distribution/update-config-repo.sh"
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

        Assert.False(File.Exists(Path.Combine(productRoot, "tests", "distribution-release.test.sh")));
        Assert.False(File.Exists(Path.Combine(productRoot, "tests", "promotion-version.test.sh")));

        foreach (var shellScript in Directory.EnumerateFiles(productRoot, "*.sh", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(shellScript);
            Assert.DoesNotContain(
                "distribution release assembly, verification, SSH transfer, idempotence, tamper rejection: PASS",
                text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "downstream version commit, idempotence, downgrade guard: PASS",
                text, StringComparison.Ordinal);
        }

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
        Assert.Contains("webassistant-builder", readme, StringComparison.Ordinal);
        Assert.Contains("CI_BUILD_PROJECT_PATH", readme, StringComparison.Ordinal);
        Assert.Contains("CI_RELEASE_CONFIG_REPO", readme, StringComparison.Ordinal);
        Assert.Contains("CI_GITLAB_CICD_AGENT_USER", readme, StringComparison.Ordinal);
        Assert.Contains("release-version.txt", readme, StringComparison.Ordinal);
        Assert.Contains("build-altlinux-release", readme, StringComparison.Ordinal);
        Assert.Contains("prepare-release-storage", readme, StringComparison.Ordinal);
        Assert.Contains("<CI_PROJECT_NAME>/releases/<VERSION>/", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Если групповой корень уже заканчивается на", readme, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA", readme, StringComparison.Ordinal);
        Assert.Contains("CI_SSH_ID_RSA_SECRET", readme, StringComparison.Ordinal);
        Assert.Contains("копирование → проверка → удаление временного каталога", readme, StringComparison.Ordinal);
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
    public async Task DevelopmentRepo_DistributionShellRegression_Passes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var testScript = Path.Combine(FindRepositoryRoot(), "tests", "distribution", "distribution-release.test.sh");
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


    [Fact]
    public async Task DevelopmentRepo_DownstreamVersionHandoff_UsesLocalGitAndRejectsDowngrades()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = Path.Combine(FindRepositoryRoot(), "tests", "distribution", "promotion-version.test.sh");
        var info = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(script);
        using var process = Process.Start(info) ??
            throw new InvalidOperationException("Не удалось запустить тест передачи версии.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0,
            $"Git version handoff failed. stdout: {await stdout}\\nstderr: {await stderr}");
        Assert.Contains("downstream version commit, idempotence, downgrade guard: PASS",
            await stdout, StringComparison.Ordinal);
    }

    private static string GetTopLevelYamlBlock(string yaml, string key)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var marker = key + ":";
        var start = Array.FindIndex(lines, line => string.Equals(line, marker, StringComparison.Ordinal));
        Assert.True(start >= 0, $"В GitLab CI отсутствует top-level блок {key}.");

        var end = start + 1;
        while (end < lines.Length &&
               (string.IsNullOrWhiteSpace(lines[end]) || char.IsWhiteSpace(lines[end][0])))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
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
