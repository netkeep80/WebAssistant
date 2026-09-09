using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ReleaseInfrastructureTests
{
    [Fact]
    public void Repository_DefinesExplicitFrozenMainCandidateWorkflow()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release-candidate.yml");

        Assert.True(File.Exists(workflowPath), $"Отсутствует frozen-main candidate workflow: {workflowPath}");

        var workflow = File.ReadAllText(workflowPath);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("source_sha:", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/build-installers.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/installer-acceptance.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("ci/release/resolve-accepted-main.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("ci/release/stage-draft-release.sh", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build/windows/package.bat", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build/linux/package.sh", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_DefinesFailClosedReleaseBoundaryScripts()
    {
        var root = FindRepositoryRoot();
        var releaseRoot = Path.Combine(root, "ci", "release");
        var expected = new[]
        {
            "resolve-accepted-main.sh",
            "inspect-release-state.sh",
            "verify-installer-assets.sh",
            "stage-draft-release.sh",
            "finalize-release.sh"
        };

        foreach (var fileName in expected)
        {
            var path = Path.Combine(releaseRoot, fileName);
            Assert.True(File.Exists(path), $"Отсутствует release boundary script: {path}");
            var text = File.ReadAllText(path);
            Assert.Contains("set -euo pipefail", text, StringComparison.Ordinal);
        }

        var resolver = File.ReadAllText(Path.Combine(releaseRoot, "resolve-accepted-main.sh"));
        Assert.Contains("ci-required", resolver, StringComparison.Ordinal);
        Assert.Contains("repo-guard", resolver, StringComparison.Ordinal);
        Assert.Contains("merge_commit_sha", resolver, StringComparison.Ordinal);
        Assert.Contains("webassist/VERSION", resolver, StringComparison.Ordinal);

        var verifier = File.ReadAllText(Path.Combine(releaseRoot, "verify-installer-assets.sh"));
        Assert.Contains("provenance", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceSha", verifier, StringComparison.Ordinal);
        Assert.Contains("version", verifier, StringComparison.OrdinalIgnoreCase);

        var staging = File.ReadAllText(Path.Combine(releaseRoot, "stage-draft-release.sh"));
        Assert.Contains("draft", staging, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v${", staging, StringComparison.Ordinal);

        var finalizer = File.ReadAllText(Path.Combine(releaseRoot, "finalize-release.sh"));
        Assert.Contains("WebAssistant-Installation-Guide.pdf", finalizer, StringComparison.Ordinal);
        Assert.Contains("docs/installation-guide/verify.sh", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", finalizer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package.bat", finalizer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package.sh", finalizer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateConformance_ReleaseVectorReferencesExecutableInfrastructureEvidence()
    {
        var root = FindRepositoryRoot();
        var conformancePath = Path.Combine(root, "contracts", "webassistant-conformance-v0.3.json");
        var text = File.ReadAllText(conformancePath);

        Assert.True(
            text.Count(character => character == '\n') > 50,
            "Candidate conformance must remain human-reviewable pretty JSON, not a one-line serialization.");

        using var conformance = JsonDocument.Parse(text);
        Assert.Equal("candidate", conformance.RootElement.GetProperty("status").GetString());
        Assert.False(conformance.RootElement.GetProperty("accepted").GetBoolean());

        var vector = conformance.RootElement
            .GetProperty("vectors")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "WA-C-RELEASE-SAME-BYTES-001");

        var evidence = vector.GetProperty("evidence")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => item is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
                 {
                     "tests/core/ReleaseInfrastructureTests.cs",
                     ".github/workflows/release-candidate.yml",
                     "ci/release/resolve-accepted-main.sh",
                     "ci/release/verify-installer-assets.sh",
                     "ci/release/stage-draft-release.sh",
                     "ci/release/finalize-release.sh"
                 })
        {
            Assert.Contains(required, evidence);
        }
    }

    [Fact]
    public void ReleaseInfrastructure_IsClassifiedAsFullDistributionWork()
    {
        var root = FindRepositoryRoot();
        var classifier = File.ReadAllText(Path.Combine(root, "ci", "change-plan.sh"));

        Assert.Contains("release-candidate.yml", classifier, StringComparison.Ordinal);
        Assert.Contains("ci/release", classifier, StringComparison.Ordinal);
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

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
