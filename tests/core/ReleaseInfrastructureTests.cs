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
        var result = RunReleaseScript(
            "verify-installer-assets.sh",
            [fixture.Root, Version, SourceSha],
            new Dictionary<string, string>(),
            null);

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

        var result = RunReleaseScript(
            "verify-installer-assets.sh",
            [fixture.Root, Version, SourceSha],
            new Dictionary<string, string>(),
            null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sha256", result.Error, StringComparison.OrdinalIgnoreCase);
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

    private static ProcessResult RunReleaseScript(
        string scriptName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? pathPrefix)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "ci", "release", scriptName);
        Assert.True(File.Exists(script), $"Отсутствует release script: {script}");

        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (pathPrefix is not null)
        {
            startInfo.Environment["PATH"] = pathPrefix + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Не удалось запустить {scriptName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value));

    private static string EncodeContent(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

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

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class ResolverFixture : IDisposable
    {
        private ResolverFixture(string root, string binDirectory, Dictionary<string, string> environment)
        {
            Root = root;
            BinDirectory = binDirectory;
            Environment = environment;
        }

        public string Root { get; }
        public string BinDirectory { get; }
        public Dictionary<string, string> Environment { get; }

        public static ResolverFixture Create(
            string? mainSha = null,
            bool includePr = true,
            string repoGuardConclusion = "success",
            string sourceVersion = Version)
        {
            var root = Directory.CreateTempSubdirectory("webassistant-release-resolver-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;

            WriteJson(Path.Combine(data, "main.json"), new { commit = new { sha = mainSha ?? SourceSha } });
            WriteJson(
                Path.Combine(data, "pulls.json"),
                includePr
                    ? new object[]
                    {
                        new
                        {
                            number = 17,
                            merged_at = "2026-09-09T19:40:05Z",
                            merge_commit_sha = SourceSha,
                            head = new { sha = PrHeadSha },
                            @base = new { @ref = "main", sha = BaseSha }
                        }
                    }
                    : Array.Empty<object>());
            WriteJson(
                Path.Combine(data, "runs.json"),
                new
                {
                    workflow_runs = new object[]
                    {
                        new
                        {
                            path = ".github/workflows/ci.yml",
                            event_name = "pull_request",
                            status = "completed",
                            conclusion = "success",
                            head_sha = PrHeadSha,
                            pull_requests = new[] { new { number = 17 } }
                        },
                        new
                        {
                            path = ".github/workflows/repo-guard.yml",
                            event_name = "pull_request",
                            status = "completed",
                            conclusion = repoGuardConclusion,
                            head_sha = PrHeadSha,
                            pull_requests = new[] { new { number = 17 } }
                        }
                    }
                });
            WriteJson(Path.Combine(data, "source-version.json"), new { content = EncodeContent(sourceVersion + "\n") });
            WriteJson(Path.Combine(data, "base-version.json"), new { content = EncodeContent(BaseVersion + "\n") });

            var fakeGh = Path.Combine(bin, "gh");
            File.WriteAllText(
                fakeGh,
                """
                #!/usr/bin/env bash
                set -euo pipefail
                [[ "${1:-}" == "api" ]] || { echo "fake gh only supports api" >&2; exit 91; }
                endpoint="${2:-}"
                case "$endpoint" in
                  "repos/test/repo/branches/main") cat "$FAKE_GH_DIR/main.json" ;;
                  "repos/test/repo/commits/$FAKE_SOURCE/pulls") cat "$FAKE_GH_DIR/pulls.json" ;;
                  "repos/test/repo/actions/runs?head_sha=$FAKE_HEAD&event=pull_request&per_page=100") cat "$FAKE_GH_DIR/runs.json" ;;
                  "repos/test/repo/contents/webassist/VERSION?ref=$FAKE_SOURCE") cat "$FAKE_GH_DIR/source-version.json" ;;
                  "repos/test/repo/contents/webassist/VERSION?ref=$FAKE_BASE") cat "$FAKE_GH_DIR/base-version.json" ;;
                  *) echo "unexpected fake gh endpoint: $endpoint" >&2; exit 92 ;;
                esac
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    fakeGh,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return new ResolverFixture(
                root,
                bin,
                new Dictionary<string, string>
                {
                    ["GH_REPO"] = "test/repo",
                    ["FAKE_GH_DIR"] = data,
                    ["FAKE_SOURCE"] = SourceSha,
                    ["FAKE_BASE"] = BaseSha,
                    ["FAKE_HEAD"] = PrHeadSha
                });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class InstallerFixture : IDisposable
    {
        private InstallerFixture(string root, string windowsArtifact)
        {
            Root = root;
            WindowsArtifact = windowsArtifact;
        }

        public string Root { get; }
        public string WindowsArtifact { get; }

        public static InstallerFixture Create()
        {
            var root = Directory.CreateTempSubdirectory("webassistant-release-assets-").FullName;
            var windows = Path.Combine(root, $"WebAssistant-win-x64-{Version}.exe");
            var linux = Path.Combine(root, $"WebAssistant-linux-x64-{Version}.zip");
            File.WriteAllText(windows, "windows installer bytes\n");
            File.WriteAllText(linux, "linux installer bytes\n");
            WriteSidecars(windows, "win-x64");
            WriteSidecars(linux, "linux-x64");
            return new InstallerFixture(root, windows);
        }

        private static void WriteSidecars(string artifact, string rid)
        {
            var name = Path.GetFileName(artifact);
            var digest = Sha256(artifact);
            File.WriteAllText(artifact + ".sha256", $"{digest}  {name}\n");
            WriteJson(
                artifact + ".provenance.json",
                new
                {
                    artifact = name,
                    version = Version,
                    sourceSha = SourceSha,
                    rid,
                    sha256 = digest,
                    size = new FileInfo(artifact).Length,
                    sdkVersion = "10.0.401",
                    configMode = "generated-default",
                    packageEntrypoint = rid == "win-x64" ? "build/windows/package.bat" : "build/linux/package.sh"
                });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
