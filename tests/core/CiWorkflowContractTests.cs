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

        Assert.DoesNotContain("Declare conservative baseline requirements", ci, StringComparison.Ordinal);
        Assert.Contains("ci/change-plan.sh", ci, StringComparison.Ordinal);
        Assert.Contains("github.event.pull_request.base.sha", ci, StringComparison.Ordinal);
        Assert.Contains("github.event.pull_request.head.sha", ci, StringComparison.Ordinal);
        Assert.Contains("installer_linux:", ci, StringComparison.Ordinal);
        Assert.Contains("installer_windows:", ci, StringComparison.Ordinal);
        Assert.Contains("virtual_linux:", ci, StringComparison.Ordinal);
        Assert.Contains("virtual_windows:", ci, StringComparison.Ordinal);
        Assert.Contains("run_linux:", ci, StringComparison.Ordinal);
        Assert.Contains("run_windows:", ci, StringComparison.Ordinal);

        AssertReusableComponent(workflows, "core.yml");
        AssertReusableComponent(workflows, "linux-systemd.yml");
        AssertReusableComponent(workflows, "windows-service.yml");
        AssertReusableComponent(workflows, "virtual-scanner.yml");

        var repoGuard = File.ReadAllText(Path.Combine(workflows, "repo-guard.yml"));
        Assert.Contains("name: repo-guard", repoGuard, StringComparison.Ordinal);
        Assert.Contains("pull_request:", repoGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributionCi_BuildsInstallersOnceAndConsumesExactUploadedArtifacts()
    {
        var root = FindRepositoryRoot();
        var workflows = Path.Combine(root, ".github", "workflows");
        var build = ReadRequired(Path.Combine(workflows, "build-installers.yml"));
        var acceptance = ReadRequired(Path.Combine(workflows, "installer-acceptance.yml"));
        var ci = ReadRequired(Path.Combine(workflows, "ci.yml"));

        Assert.Contains("workflow_call:", build, StringComparison.Ordinal);
        Assert.Contains("source_ref:", build, StringComparison.Ordinal);
        Assert.Contains("run_linux:", build, StringComparison.Ordinal);
        Assert.Contains("run_windows:", build, StringComparison.Ordinal);
        Assert.Contains("build-linux-installer:", build, StringComparison.Ordinal);
        Assert.Contains("build-windows-installer:", build, StringComparison.Ordinal);
        Assert.Contains("build/linux/package.sh", build, StringComparison.Ordinal);
        Assert.Contains("build/windows/package.bat", build, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v4", build, StringComparison.Ordinal);
        Assert.Contains("webassistant-linux-installer-${{ inputs.source_ref }}", build, StringComparison.Ordinal);
        Assert.Contains("webassistant-windows-installer-${{ inputs.source_ref }}", build, StringComparison.Ordinal);
        Assert.DoesNotContain("run-installer-acceptance", build, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("workflow_call:", acceptance, StringComparison.Ordinal);
        Assert.Contains("source_ref:", acceptance, StringComparison.Ordinal);
        Assert.Contains("run_linux:", acceptance, StringComparison.Ordinal);
        Assert.Contains("run_windows:", acceptance, StringComparison.Ordinal);
        Assert.Contains("actions/download-artifact@v4", acceptance, StringComparison.Ordinal);
        Assert.Contains("tests/linux-systemd/run-installer-acceptance.sh", acceptance, StringComparison.Ordinal);
        Assert.Contains("tests/windows-service/run-installer-acceptance.ps1", acceptance, StringComparison.Ordinal);
        Assert.DoesNotContain("build/linux/package.sh", acceptance, StringComparison.Ordinal);
        Assert.DoesNotContain("build/windows/package.bat", acceptance, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", acceptance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actions/setup-dotnet", acceptance, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("build-installers:", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/build-installers.yml", ci, StringComparison.Ordinal);
        Assert.Contains("installer-acceptance:", ci, StringComparison.Ordinal);
        Assert.Contains("- build-installers", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/installer-acceptance.yml", ci, StringComparison.Ordinal);
        Assert.Contains("needs.requirements.outputs.installer_linux", ci, StringComparison.Ordinal);
        Assert.Contains("needs.requirements.outputs.installer_windows", ci, StringComparison.Ordinal);
        Assert.Contains("INSTALLER_ACCEPTANCE_RESULT: ${{ needs.installer-acceptance.result }}", ci, StringComparison.Ordinal);
        Assert.Contains("check_result installer-acceptance", ci, StringComparison.Ordinal);

        Assert.Contains("linux-systemd:", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/linux-systemd.yml", ci, StringComparison.Ordinal);
        Assert.Contains("needs.requirements.outputs.installer_linux != 'true'", ci, StringComparison.Ordinal);
        Assert.Contains("windows-service:", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/windows-service.yml", ci, StringComparison.Ordinal);
        Assert.Contains("needs.requirements.outputs.installer_windows != 'true'", ci, StringComparison.Ordinal);

        Assert.Contains("scanner-final:", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/virtual-scanner.yml", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void AltP11Acceptance_RemovesI586CompatibilityRepositoryBeforeAptUpdate()
    {
        var root = FindRepositoryRoot();
        var harness = ReadRequired(Path.Combine(
            root,
            "tests",
            "linux-systemd",
            "run-systemd-acceptance.sh"));

        const string repositoryMarker = "ALT_I586_REPOSITORY='p11/branch/x86_64-i586'";
        var repositoryMarkerIndex = harness.IndexOf(repositoryMarker, StringComparison.Ordinal);
        var restrictionIndex = harness.IndexOf("sed -i", StringComparison.Ordinal);
        var aptUpdateIndex = harness.IndexOf("apt-get update", StringComparison.Ordinal);

        Assert.True(repositoryMarkerIndex >= 0, "ALT p11 acceptance must name the i586 compatibility repository explicitly.");
        Assert.True(restrictionIndex >= 0, "ALT p11 acceptance must remove the i586 compatibility repository from test sources.");
        Assert.True(aptUpdateIndex >= 0, "ALT p11 acceptance must still update native repository metadata.");
        Assert.True(restrictionIndex < aptUpdateIndex, "The i586 compatibility repository must be removed before apt-get update.");
    }

    [Fact]
    public void AltP11Acceptance_KeepsFiniteFortyFiveMinuteHangGuard()
    {
        var root = FindRepositoryRoot();
        var workflow = ReadRequired(Path.Combine(root, ".github", "workflows", "linux-systemd.yml"));
        var jobIndex = workflow.IndexOf("alt-p11-systemd-acceptance:", StringComparison.Ordinal);

        Assert.True(jobIndex >= 0, "ALT p11 acceptance job must remain present.");
        var job = workflow[jobIndex..];
        Assert.Contains("timeout-minutes: 45", job, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUpgradeAcceptance_WaitsForExactProcessIdentityToDisappearWithinBoundedDeadline()
    {
        var root = FindRepositoryRoot();
        var harness = ReadRequired(Path.Combine(
            root,
            "tests",
            "windows-service",
            "run-upgrade-acceptance.ps1"));
        var functionStart = harness.IndexOf("function Assert-ProcessIdentityGone", StringComparison.Ordinal);
        var functionEnd = harness.IndexOf("function Assert-NoFilesInUseEvidence", functionStart, StringComparison.Ordinal);

        Assert.True(functionStart >= 0, "Windows upgrade acceptance must define Assert-ProcessIdentityGone.");
        Assert.True(functionEnd > functionStart, "Windows upgrade acceptance must keep Assert-ProcessIdentityGone as a bounded helper.");

        var helper = harness[functionStart..functionEnd];
        Assert.Contains("[DateTime]::UtcNow.AddSeconds(", helper, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Id", helper, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds", helper, StringComparison.Ordinal);
        Assert.Contains("while ([DateTime]::UtcNow -lt $deadline)", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseResolver_UsesGitHubWorkflowRunEventField()
    {
        var root = FindRepositoryRoot();
        var resolver = ReadRequired(Path.Combine(root, "ci", "release", "resolve-accepted-main.sh"));

        // GitHub Actions workflow-run REST payloads expose the trigger as `event`.
        // The resolver must consume that live field; synthetic fixtures must not be its only proof.
        Assert.Contains("run.get(\"event\")", resolver, StringComparison.Ordinal);
    }

    private static string ReadRequired(string path)
    {
        Assert.True(File.Exists(path), $"Required workflow is missing: {path}");
        return File.ReadAllText(path);
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
