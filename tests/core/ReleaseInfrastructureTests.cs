using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ReleaseInfrastructureTests
{
    private const string SourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PrHeadSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Version = "0.3.19";
    private const string BaseVersion = "0.3.18";

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
        Assert.Contains("--require-complete", workflow, StringComparison.Ordinal);
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
        var expected = new[] { "resolve-accepted-main.sh", "inspect-release-state.sh", "verify-installer-assets.sh", "stage-draft-release.sh", "finalize-release.sh" };
        foreach (var fileName in expected)
        {
            var path = Path.Combine(releaseRoot, fileName);
            Assert.True(File.Exists(path), $"Отсутствует release boundary script: {path}");
            Assert.Contains("set -euo pipefail", File.ReadAllText(path), StringComparison.Ordinal);
        }

        var resolver = File.ReadAllText(Path.Combine(releaseRoot, "resolve-accepted-main.sh"));
        Assert.Contains("ci-required", resolver, StringComparison.Ordinal);
        Assert.Contains("repo-guard", resolver, StringComparison.Ordinal);
        Assert.Contains("merge_commit_sha", resolver, StringComparison.Ordinal);
        Assert.Contains("webassist/VERSION", resolver, StringComparison.Ordinal);
        Assert.Contains("/jobs?per_page=100", resolver, StringComparison.Ordinal);

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
    public void AcceptedMainResolver_AcceptsExactMergedPrWithGreenOwnedWorkflowsAndMonotonicVersion()
    {
        using var fixture = ResolverFixture.Create();
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(SourceSha, json.RootElement.GetProperty("sourceSha").GetString());
        Assert.Equal(PrHeadSha, json.RootElement.GetProperty("prHeadSha").GetString());
        Assert.Equal(Version, json.RootElement.GetProperty("version").GetString());
        Assert.Equal(17, json.RootElement.GetProperty("acceptedPr").GetInt32());
        Assert.Equal(101, json.RootElement.GetProperty("ciRunId").GetInt32());
        Assert.Equal(102, json.RootElement.GetProperty("repoGuardRunId").GetInt32());
    }

    [Fact]
    public void AcceptedMainResolver_RejectsSourceShaDifferentFromCurrentMain()
    {
        using var fixture = ResolverFixture.Create(mainSha: new string('d', 40));
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("current main", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsDirectPushWithoutExactMergedPr()
    {
        using var fixture = ResolverFixture.Create(includePr: false);
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("merged PR", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsMissingGreenRepoGuardEvidence()
    {
        using var fixture = ResolverFixture.Create(repoGuardConclusion: "failure");
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("repo-guard", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsMissingGreenCiRequiredJobEvidence()
    {
        using var fixture = ResolverFixture.Create(ciRequiredConclusion: "failure");
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ci-required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsMissingGreenRepoGuardJobEvidence()
    {
        using var fixture = ResolverFixture.Create(repoGuardJobConclusion: "failure");
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("repo-guard", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsNonMonotonicVersionTransition()
    {
        using var fixture = ResolverFixture.Create(sourceVersion: BaseVersion);
        var result = RunReleaseScript("resolve-accepted-main.sh", [SourceSha], fixture.Environment, fixture.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("strictly greater", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerAssetVerifier_AcceptsExactCanonicalSixFileIdentity()
    {
        using var fixture = InstallerFixture.Create();
        var result = RunReleaseScript("verify-installer-assets.sh", [fixture.Root, Version, SourceSha], new Dictionary<string, string>(), null);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(Version, json.RootElement.GetProperty("version").GetString());
        Assert.Equal(SourceSha, json.RootElement.GetProperty("sourceSha").GetString());
    }

    [Fact]
    public void InstallerAssetVerifier_RejectsChangedBytesAfterSidecarCreation()
    {
        using var fixture = InstallerFixture.Create();
        File.AppendAllText(fixture.WindowsArtifact, "tamper");
        var result = RunReleaseScript("verify-installer-assets.sh", [fixture.Root, Version, SourceSha], new Dictionary<string, string>(), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sha256", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DraftStaging_RejectsConflictingExistingAssetDigest()
    {
        using var fixture = InstallerFixture.Create();
        using var github = ReleaseGhFixture.Create("draft-conflict", fixture);
        var result = RunReleaseScript("stage-draft-release.sh", [fixture.Root, Version, SourceSha, "555"], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("digest", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DraftStaging_CreatesUnpublishedDraftAndStagesSixExactAssets()
    {
        using var fixture = InstallerFixture.Create();
        using var github = ReleaseGhFixture.Create("absent", fixture);
        var result = RunReleaseScript("stage-draft-release.sh", [fixture.Root, Version, SourceSha, "555"], github.Environment, github.BinDirectory);
        Assert.Equal(0, result.ExitCode);
        var log = File.ReadAllText(github.LogPath);
        Assert.Contains("POST repos/test/repo/releases", log, StringComparison.Ordinal);
        Assert.Contains("release upload v0.3.19", log, StringComparison.Ordinal);
        Assert.DoesNotContain("--clobber", log, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftStaging_ReverifiesRemoteDigestAfterUpload()
    {
        using var fixture = InstallerFixture.Create();
        using var github = ReleaseGhFixture.Create("upload-remote-conflict", fixture);
        var result = RunReleaseScript("stage-draft-release.sh", [fixture.Root, Version, SourceSha, "555"], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("digest", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release upload v0.3.19", File.ReadAllText(github.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDraftPreflight_RejectsUnprovenCandidateRun()
    {
        using var fixture = InstallerFixture.Create();
        using var github = ReleaseGhFixture.Create("draft-unproven", fixture);
        var result = RunReleaseScript("inspect-release-state.sh", [Version, SourceSha, "--require-complete"], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("candidate run", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingPublishedPreflight_RejectsIncompletePublishedRelease()
    {
        using var fixture = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(fixture);
        using var github = ReleaseGhFixture.Create("published-incomplete", fixture, evidence);
        var result = RunReleaseScript("inspect-release-state.sh", [Version, SourceSha, "--require-complete"], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("asset", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Finalizer_RejectsPublicationWhenMainMovedAfterFreeze()
    {
        using var github = ReleaseGhFixture.Create("complete-draft", mainSha: new string('d', 40));
        var root = FindRepositoryRoot();
        var evidence = Path.Combine(root, "tests", "core", "nonexistent-final-evidence.json");
        var pdf = Path.Combine(root, "WebAssistant-Installation-Guide.pdf");
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", evidence, "--pdf", pdf], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("current main", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release edit", File.ReadAllText(github.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Finalizer_PublishesValidCompleteDraftWithoutRebuild()
    {
        using var installers = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(installers);
        using var github = ReleaseGhFixture.Create("complete-draft", installers, evidence);
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", evidence.ManifestPath, "--pdf", evidence.PdfPath], github.Environment, github.BinDirectory);
        Assert.Equal(0, result.ExitCode);
        var log = File.ReadAllText(github.LogPath);
        Assert.Contains("release download v0.3.19", log, StringComparison.Ordinal);
        Assert.Contains("release upload v0.3.19", log, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-Installation-Guide.pdf", log, StringComparison.Ordinal);
        Assert.Contains("release edit v0.3.19", log, StringComparison.Ordinal);
        Assert.Contains("--draft=false", log, StringComparison.Ordinal);
        Assert.DoesNotContain("--clobber", log, StringComparison.Ordinal);
        Assert.DoesNotContain("package.bat", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package.sh", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet publish", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Finalizer_RejectsEvidenceInstallerHashMismatch()
    {
        using var installers = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(installers, wrongWindowsHash: true);
        using var github = ReleaseGhFixture.Create("complete-draft", installers, evidence);
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", evidence.ManifestPath, "--pdf", evidence.PdfPath], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("manifest installer", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release edit", File.ReadAllText(github.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Finalizer_RejectsConflictingExistingPdfDigest()
    {
        using var installers = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(installers);
        using var github = ReleaseGhFixture.Create("draft-pdf-conflict", installers, evidence);
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", evidence.ManifestPath, "--pdf", evidence.PdfPath], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("PDF", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("digest", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release edit", File.ReadAllText(github.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Finalizer_RejectsUnexpectedPublicAsset()
    {
        using var installers = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(installers);
        using var github = ReleaseGhFixture.Create("draft-extra", installers, evidence);
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", evidence.ManifestPath, "--pdf", evidence.PdfPath], github.Environment, github.BinDirectory);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unexpected", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release edit", File.ReadAllText(github.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Finalizer_MatchingPublishedReleaseIsNoOpWithoutLocalEvidence()
    {
        using var installers = InstallerFixture.Create();
        using var evidence = FinalEvidenceFixture.Create(installers);
        using var github = ReleaseGhFixture.Create("published-complete", installers, evidence);
        var missingEvidence = Path.Combine(evidence.Root, "already-published-evidence-not-needed.json");
        var missingPdf = Path.Combine(evidence.Root, "already-published-pdf-not-needed.pdf");
        var result = RunReleaseScript("finalize-release.sh", ["--source-sha", SourceSha, "--evidence", missingEvidence, "--pdf", missingPdf], github.Environment, github.BinDirectory);
        Assert.Equal(0, result.ExitCode);
        var log = File.ReadAllText(github.LogPath);
        Assert.DoesNotContain("release upload", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release edit", log, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateConformance_ReleaseVectorReferencesExecutableInfrastructureEvidence()
    {
        var root = FindRepositoryRoot();
        var conformancePath = Path.Combine(root, "contracts", "webassistant-conformance-v0.3.json");
        var text = File.ReadAllText(conformancePath);
        Assert.True(text.Count(character => character == '\n') > 50, "Candidate conformance must remain human-reviewable pretty JSON, not a one-line serialization.");
        using var conformance = JsonDocument.Parse(text);
        Assert.Equal("candidate", conformance.RootElement.GetProperty("status").GetString());
        Assert.False(conformance.RootElement.GetProperty("accepted").GetBoolean());

        var vector = conformance.RootElement.GetProperty("vectors").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "WA-C-RELEASE-SAME-BYTES-001");
        var vectorEvidence = vector.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tests/core/DistributionContractCandidateTests.cs", vectorEvidence);
        Assert.Contains("tests/core/ReleaseInfrastructureTests.cs", vectorEvidence);

        var requiredPaths = conformance.RootElement.GetProperty("requiredRepositoryPaths").EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { ".github/workflows/release-candidate.yml", "ci/release/resolve-accepted-main.sh", "ci/release/inspect-release-state.sh", "ci/release/verify-installer-assets.sh", "ci/release/stage-draft-release.sh", "ci/release/finalize-release.sh" })
            Assert.Contains(required, requiredPaths);
    }

    [Fact]
    public void ReleaseInfrastructure_IsClassifiedAsFullDistributionWork()
    {
        var root = FindRepositoryRoot();
        var classifier = File.ReadAllText(Path.Combine(root, "ci", "change-plan.sh"));
        Assert.Contains("release-candidate.yml", classifier, StringComparison.Ordinal);
        Assert.Contains("ci/release", classifier, StringComparison.Ordinal);
    }

    private static ProcessResult RunReleaseScript(string scriptName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string? pathPrefix)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "ci", "release", scriptName);
        Assert.True(File.Exists(script), $"Отсутствует release script: {script}");
        var startInfo = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (pathPrefix is not null) startInfo.Environment["PATH"] = pathPrefix + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Не удалось запустить {scriptName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
    private static string EncodeContent(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) && Directory.Exists(Path.Combine(directory.FullName, "tests")) && Directory.Exists(Path.Combine(directory.FullName, ".github"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class ResolverFixture : IDisposable
    {
        private ResolverFixture(string root, string binDirectory, Dictionary<string, string> environment) { Root = root; BinDirectory = binDirectory; Environment = environment; }
        public string Root { get; }
        public string BinDirectory { get; }
        public Dictionary<string, string> Environment { get; }

        public static ResolverFixture Create(string? mainSha = null, bool includePr = true, string repoGuardConclusion = "success", string ciRequiredConclusion = "success", string repoGuardJobConclusion = "success", string sourceVersion = Version)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-release-resolver-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            WriteJson(Path.Combine(data, "main.json"), new { commit = new { sha = mainSha ?? SourceSha } });
            WriteJson(Path.Combine(data, "pulls.json"), includePr ? new object[] { new { number = 17, merged_at = "2026-09-09T19:40:05Z", merge_commit_sha = SourceSha, head = new { sha = PrHeadSha }, @base = new { @ref = "main", sha = BaseSha } } } : Array.Empty<object>());
            WriteJson(Path.Combine(data, "runs.json"), new { workflow_runs = new object[] { new { id = 101, path = ".github/workflows/ci.yml", event_name = "pull_request", status = "completed", conclusion = "success", head_sha = PrHeadSha, pull_requests = new[] { new { number = 17 } } }, new { id = 102, path = ".github/workflows/repo-guard.yml", event_name = "pull_request", status = "completed", conclusion = repoGuardConclusion, head_sha = PrHeadSha, pull_requests = new[] { new { number = 17 } } } } });
            WriteJson(Path.Combine(data, "ci-jobs.json"), new { jobs = new object[] { new { name = "ci-required", status = "completed", conclusion = ciRequiredConclusion } } });
            WriteJson(Path.Combine(data, "repo-guard-jobs.json"), new { jobs = new object[] { new { name = "repo-guard", status = "completed", conclusion = repoGuardJobConclusion } } });
            WriteJson(Path.Combine(data, "source-version.json"), new { content = EncodeContent(sourceVersion + "\n") });
            WriteJson(Path.Combine(data, "base-version.json"), new { content = EncodeContent(BaseVersion + "\n") });
            var fakeGh = Path.Combine(bin, "gh");
            File.WriteAllText(fakeGh, """
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == "api" ]] || { echo "fake gh only supports api" >&2; exit 91; }
endpoint="${2:-}"
case "$endpoint" in
  "repos/test/repo/branches/main") cat "$FAKE_GH_DIR/main.json" ;;
  "repos/test/repo/commits/$FAKE_SOURCE/pulls") cat "$FAKE_GH_DIR/pulls.json" ;;
  "repos/test/repo/actions/runs?head_sha=$FAKE_HEAD&event=pull_request&per_page=100") cat "$FAKE_GH_DIR/runs.json" ;;
  "repos/test/repo/actions/runs/101/jobs?per_page=100") cat "$FAKE_GH_DIR/ci-jobs.json" ;;
  "repos/test/repo/actions/runs/102/jobs?per_page=100") cat "$FAKE_GH_DIR/repo-guard-jobs.json" ;;
  "repos/test/repo/contents/webassist/VERSION?ref=$FAKE_SOURCE") cat "$FAKE_GH_DIR/source-version.json" ;;
  "repos/test/repo/contents/webassist/VERSION?ref=$FAKE_BASE") cat "$FAKE_GH_DIR/base-version.json" ;;
  *) echo "unexpected fake gh endpoint: $endpoint" >&2; exit 92 ;;
esac
""");
            SetExecutable(fakeGh);
            return new ResolverFixture(root, bin, new Dictionary<string, string> { ["GH_REPO"] = "test/repo", ["FAKE_GH_DIR"] = data, ["FAKE_SOURCE"] = SourceSha, ["FAKE_BASE"] = BaseSha, ["FAKE_HEAD"] = PrHeadSha });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class InstallerFixture : IDisposable
    {
        private InstallerFixture(string root, string windowsArtifact, string linuxArtifact) { Root = root; WindowsArtifact = windowsArtifact; LinuxArtifact = linuxArtifact; }
        public string Root { get; }
        public string WindowsArtifact { get; }
        public string LinuxArtifact { get; }
        public string WindowsSha => Sha256(WindowsArtifact);
        public string LinuxSha => Sha256(LinuxArtifact);
        public IReadOnlyList<string> SixAssetPaths => [WindowsArtifact, WindowsArtifact + ".sha256", WindowsArtifact + ".provenance.json", LinuxArtifact, LinuxArtifact + ".sha256", LinuxArtifact + ".provenance.json"];

        public static InstallerFixture Create()
        {
            var root = Directory.CreateTempSubdirectory("webassistant-release-assets-").FullName;
            var windows = Path.Combine(root, $"WebAssistant-win-x64-{Version}.exe");
            var linux = Path.Combine(root, $"WebAssistant-linux-x64-{Version}.zip");
            File.WriteAllText(windows, "windows installer bytes\n");
            File.WriteAllText(linux, "linux installer bytes\n");
            WriteSidecars(windows, "win-x64");
            WriteSidecars(linux, "linux-x64");
            return new InstallerFixture(root, windows, linux);
        }

        private static void WriteSidecars(string artifact, string rid)
        {
            var name = Path.GetFileName(artifact);
            var digest = Sha256(artifact);
            File.WriteAllText(artifact + ".sha256", $"{digest}  {name}\n");
            WriteJson(artifact + ".provenance.json", new { artifact = name, version = Version, sourceSha = SourceSha, rid, sha256 = digest, size = new FileInfo(artifact).Length, sdkVersion = "10.0.401", configMode = "generated-default", packageEntrypoint = rid == "win-x64" ? "build/windows/package.bat" : "build/linux/package.sh" });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FinalEvidenceFixture : IDisposable
    {
        private static readonly (string Slot, string Platform)[] CaptureSlots =
        [
            ("WIN_ARTIFACT_IDENTITY", "windows"),
            ("WIN_INSTALL_UAC", "windows"),
            ("WIN_INSTALLED_APPS", "windows"),
            ("WIN_SERVICE_HEALTH", "windows"),
            ("WIN_UNINSTALL", "windows"),
            ("ALT_ARTIFACT_IDENTITY", "alt-linux"),
            ("ALT_INSTALL", "alt-linux"),
            ("ALT_SYSTEMD_HEALTH", "alt-linux"),
            ("ALT_RESTART", "alt-linux"),
            ("ALT_UNINSTALL", "alt-linux")
        ];

        private FinalEvidenceFixture(string root, string manifestPath, string pdfPath) { Root = root; ManifestPath = manifestPath; PdfPath = pdfPath; }
        public string Root { get; }
        public string ManifestPath { get; }
        public string PdfPath { get; }
        public string PdfSha => Sha256(PdfPath);

        public static FinalEvidenceFixture Create(InstallerFixture installers, bool wrongWindowsHash = false)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-final-evidence-").FullName;
            var captures = new List<object>();
            foreach (var (slot, platform) in CaptureSlots)
            {
                var fileName = slot + ".png";
                var path = Path.Combine(root, fileName);
                File.WriteAllText(path, $"capture {slot}\n");
                captures.Add(new { slot, platform, path = fileName, sha256 = Sha256(path), kind = "final" });
            }

            var windowsHash = wrongWindowsHash ? new string('d', 64) : installers.WindowsSha;
            var manifestPath = Path.Combine(root, "installation-evidence.json");
            WriteJson(manifestPath, new
            {
                schema = "webassistant-installation-evidence/v1",
                kind = "final",
                sourceSha = SourceSha,
                version = Version,
                artifacts = new
                {
                    windows = new { filename = Path.GetFileName(installers.WindowsArtifact), sha256 = windowsHash },
                    linux = new { filename = Path.GetFileName(installers.LinuxArtifact), sha256 = installers.LinuxSha }
                },
                altTarget = new { osName = "ALT Linux", osVersion = "10.1", completed = true },
                captures
            });

            var pdfPath = Path.Combine(root, "WebAssistant-Installation-Guide.pdf");
            File.WriteAllText(pdfPath, "fixture final PDF bytes\n");
            return new FinalEvidenceFixture(root, manifestPath, pdfPath);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ReleaseGhFixture : IDisposable
    {
        private ReleaseGhFixture(string root, string bin, string log, Dictionary<string, string> environment) { Root = root; BinDirectory = bin; LogPath = log; Environment = environment; }
        public string Root { get; }
        public string BinDirectory { get; }
        public string LogPath { get; }
        public Dictionary<string, string> Environment { get; }

        public static ReleaseGhFixture Create(string state, InstallerFixture? installers = null, FinalEvidenceFixture? evidence = null, string? mainSha = null)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-release-gh-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var remote = Directory.CreateDirectory(Path.Combine(root, "remote-assets")).FullName;
            var log = Path.Combine(root, "gh.log");
            File.WriteAllText(log, string.Empty);

            if (installers is not null)
            {
                foreach (var source in installers.SixAssetPaths)
                    File.Copy(source, Path.Combine(remote, Path.GetFileName(source)), overwrite: true);
            }
            if (evidence is not null)
                File.Copy(evidence.PdfPath, Path.Combine(remote, Path.GetFileName(evidence.PdfPath)), overwrite: true);

            var body = JsonSerializer.Serialize(new { schema = "webassistant-release-candidate/v1", sourceSha = SourceSha, version = Version, candidateRunId = 555, state = "installers-accepted-staged" });
            WriteJson(Path.Combine(data, "draft-release.json"), new object[] { new { id = 77, tag_name = "v0.3.19", target_commitish = SourceSha, draft = true, prerelease = false, body } });
            WriteJson(Path.Combine(data, "published-release.json"), new object[] { new { id = 77, tag_name = "v0.3.19", target_commitish = SourceSha, draft = false, prerelease = false, body } });
            WriteJson(Path.Combine(data, "candidate-run-success.json"), new { id = 555, path = ".github/workflows/release-candidate.yml", event = "workflow_dispatch", head_sha = SourceSha, status = "completed", conclusion = "success" });
            WriteJson(Path.Combine(data, "candidate-run-failure.json"), new { id = 555, path = ".github/workflows/release-candidate.yml", @event = "workflow_dispatch", head_sha = SourceSha, status = "completed", conclusion = "failure" });
            WriteJson(Path.Combine(data, "assets-empty.json"), Array.Empty<object>());

            var six = installers is null ? Array.Empty<object>() : AssetRecords(installers.SixAssetPaths);
            WriteJson(Path.Combine(data, "assets-six.json"), six);
            WriteJson(Path.Combine(data, "assets-incomplete.json"), six.Take(Math.Max(0, six.Length - 1)).ToArray());
            var conflict = six.Length == 0 ? Array.Empty<object>() : six.Select((asset, index) => index == 0 ? new { name = ReadName(asset), digest = "sha256:" + new string('d', 64) } : asset).ToArray();
            WriteJson(Path.Combine(data, "assets-conflict.json"), conflict);

            var seven = six.ToList();
            if (evidence is not null) seven.Add(new { name = "WebAssistant-Installation-Guide.pdf", digest = "sha256:" + evidence.PdfSha });
            WriteJson(Path.Combine(data, "assets-seven.json"), seven);
            var pdfConflict = six.ToList();
            pdfConflict.Add(new { name = "WebAssistant-Installation-Guide.pdf", digest = "sha256:" + new string('e', 64) });
            WriteJson(Path.Combine(data, "assets-pdf-conflict.json"), pdfConflict);
            var extra = six.ToList();
            extra.Add(new { name = "unexpected.txt", digest = "sha256:" + new string('f', 64) });
            WriteJson(Path.Combine(data, "assets-extra.json"), extra);

            var fakeGh = Path.Combine(bin, "gh");
            File.WriteAllText(fakeGh, """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_GH_LOG"

if [[ "${1:-}" == "api" && "${2:-}" == "repos/test/repo/branches/main" ]]; then
  printf '{"commit":{"sha":"%s"}}\n' "$FAKE_MAIN_SHA"; exit 0
fi

if [[ "${1:-}" == "api" && "${2:-}" == "repos/test/repo/releases?per_page=100" ]]; then
  case "$FAKE_RELEASE_STATE" in
    absent)
      [[ -f "$FAKE_GH_ROOT/draft-created" ]] && cat "$FAKE_GH_DATA/draft-release.json" || printf '[]\n' ;;
    upload-remote-conflict)
      [[ -f "$FAKE_GH_ROOT/draft-created" ]] && cat "$FAKE_GH_DATA/draft-release.json" || printf '[]\n' ;;
    published-complete|published-incomplete) cat "$FAKE_GH_DATA/published-release.json" ;;
    *)
      [[ -f "$FAKE_GH_ROOT/published" ]] && cat "$FAKE_GH_DATA/published-release.json" || cat "$FAKE_GH_DATA/draft-release.json" ;;
  esac
  exit 0
fi

if [[ "${1:-}" == "api" && "${2:-}" == "repos/test/repo/git/ref/tags/v0.3.19" ]]; then
  if [[ "$FAKE_RELEASE_STATE" == absent || "$FAKE_RELEASE_STATE" == upload-remote-conflict ]] && [[ ! -f "$FAKE_GH_ROOT/tag-created" ]]; then exit 1; fi
  printf '{"object":{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}\n'; exit 0
fi

if [[ "${1:-}" == "api" && "${2:-}" == "repos/test/repo/releases/77/assets?per_page=100" ]]; then
  case "$FAKE_RELEASE_STATE" in
    draft-conflict) cat "$FAKE_GH_DATA/assets-conflict.json" ;;
    upload-remote-conflict)
      [[ -f "$FAKE_GH_ROOT/installers-uploaded" ]] && cat "$FAKE_GH_DATA/assets-conflict.json" || cat "$FAKE_GH_DATA/assets-empty.json" ;;
    published-incomplete) cat "$FAKE_GH_DATA/assets-incomplete.json" ;;
    published-complete|draft-pdf-exact) cat "$FAKE_GH_DATA/assets-seven.json" ;;
    draft-pdf-conflict) cat "$FAKE_GH_DATA/assets-pdf-conflict.json" ;;
    draft-extra) cat "$FAKE_GH_DATA/assets-extra.json" ;;
    *)
      [[ -f "$FAKE_GH_ROOT/pdf-uploaded" ]] && cat "$FAKE_GH_DATA/assets-seven.json" || cat "$FAKE_GH_DATA/assets-six.json" ;;
  esac
  exit 0
fi

if [[ "${1:-}" == "api" && "${2:-}" == "repos/test/repo/actions/runs/555" ]]; then
  [[ "$FAKE_RELEASE_STATE" == draft-unproven ]] && cat "$FAKE_GH_DATA/candidate-run-failure.json" || cat "$FAKE_GH_DATA/candidate-run-success.json"
  exit 0
fi

if [[ "${1:-}" == "api" && "${2:-}" == "--method" && "${3:-}" == "POST" ]]; then
  endpoint="${4:-}"
  case "$endpoint" in
    repos/test/repo/git/refs) touch "$FAKE_GH_ROOT/tag-created"; printf '{"ref":"refs/tags/v0.3.19"}\n' ;;
    repos/test/repo/releases) touch "$FAKE_GH_ROOT/draft-created"; printf '{"id":77,"draft":true,"tag_name":"v0.3.19"}\n' ;;
    *) printf 'unexpected fake POST endpoint: %s\n' "$endpoint" >&2; exit 92 ;;
  esac
  exit 0
fi

if [[ "${1:-}" == "release" && "${2:-}" == "upload" ]]; then
  if printf '%s\n' "$*" | grep -Fq 'WebAssistant-Installation-Guide.pdf'; then
    touch "$FAKE_GH_ROOT/pdf-uploaded"
  else
    touch "$FAKE_GH_ROOT/installers-uploaded"
  fi
  exit 0
fi

if [[ "${1:-}" == "release" && "${2:-}" == "download" ]]; then
  target=''
  while [[ $# -gt 0 ]]; do
    if [[ "$1" == '--dir' ]]; then target="${2:-}"; break; fi
    shift
  done
  [[ -n "$target" ]] || { echo 'fake gh release download requires --dir' >&2; exit 93; }
  mkdir -p "$target"
  cp "$FAKE_REMOTE_ASSETS"/* "$target"/ 2>/dev/null || true
  exit 0
fi

if [[ "${1:-}" == "release" && "${2:-}" == "edit" ]]; then
  touch "$FAKE_GH_ROOT/published"
  exit 0
fi

printf 'unexpected fake gh: %s\n' "$*" >&2; exit 92
""");
            SetExecutable(fakeGh);
            WriteFakePdfTools(bin);

            return new ReleaseGhFixture(root, bin, log, new Dictionary<string, string>
            {
                ["GH_REPO"] = "test/repo",
                ["FAKE_GH_LOG"] = log,
                ["FAKE_RELEASE_STATE"] = state,
                ["FAKE_MAIN_SHA"] = mainSha ?? SourceSha,
                ["FAKE_GH_ROOT"] = root,
                ["FAKE_GH_DATA"] = data,
                ["FAKE_REMOTE_ASSETS"] = remote
            });
        }

        private static object[] AssetRecords(IEnumerable<string> paths) => paths.Select(path => (object)new { name = Path.GetFileName(path), digest = "sha256:" + Sha256(path) }).ToArray();
        private static string ReadName(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty("name").GetString()!;

        private static void WriteFakePdfTools(string bin)
        {
            var pdfinfo = Path.Combine(bin, "pdfinfo");
            File.WriteAllText(pdfinfo, """
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == '-v' ]]; then echo 'pdfinfo version 25.06.0'; else echo 'Pages: 1'; fi
""");
            SetExecutable(pdfinfo);

            var pdftotext = Path.Combine(bin, "pdftotext");
            File.WriteAllText(pdftotext, """
#!/usr/bin/env bash
set -euo pipefail
cat > "$2" <<'TXT'
Установка WebAssistant
Windows
ALT Linux 10.1
Windows: идентификатор и SHA-256 установщика
Windows: запуск установщика и подтверждение UAC
Windows: WebAssistant в Installed Apps и DisplayVersion
Windows: служба WebAssistant и /v1/health
Windows: стандартное удаление WebAssistant
ALT Linux 10.1: идентификатор и SHA-256 пакета
ALT Linux 10.1: установка из canonical ZIP
ALT Linux 10.1: systemd service и /v1/health
ALT Linux 10.1: перезапуск webassist.service
ALT Linux 10.1: удаление WebAssistant
TXT
""");
            SetExecutable(pdftotext);

            var pdftoppm = Path.Combine(bin, "pdftoppm");
            File.WriteAllText(pdftoppm, """
#!/usr/bin/env bash
set -euo pipefail
prefix="${@: -1}"
printf 'rendered page\n' > "${prefix}-1.png"
""");
            SetExecutable(pdftoppm);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}