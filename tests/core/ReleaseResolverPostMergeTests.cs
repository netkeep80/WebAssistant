using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ReleaseResolverPostMergeTests
{
    private const string SourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PrHeadSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string PrHeadRef = "feature/release-candidate";

    [Fact]
    public void AcceptedMainResolver_AcceptsPostMergeWorkflowRunsWithEmptyPullRequests()
    {
        using var fixture = ResolverFixture.Create();
        var result = RunResolver(fixture);

        Assert.True(result.ExitCode == 0, $"Post-merge GitHub shape must remain valid acceptance evidence. stderr: {result.Error}\nstdout: {result.Output}");
        Assert.Contains(SourceSha, result.Output, StringComparison.Ordinal);
        Assert.Contains(PrHeadSha, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsSuccessfulRunFromDifferentPrBranchWithSameHeadSha()
    {
        using var fixture = ResolverFixture.Create(runHeadBranch: "feature/foreign-pr");
        var result = RunResolver(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ci-required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMainResolver_RejectsSuccessfulRunCreatedAfterMergedPrLifecycle()
    {
        using var fixture = ResolverFixture.Create(runCreatedAt: "2026-09-09T22:05:00Z");
        var result = RunResolver(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ci-required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessResult RunResolver(ResolverFixture fixture)
    {
        var script = Path.Combine(FindRepositoryRoot(), "ci", "release", "resolve-accepted-main.sh");
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(SourceSha);
        startInfo.Environment["GH_REPO"] = "test/repo";
        startInfo.Environment["PATH"] = fixture.BinDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить accepted-main resolver.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                Directory.Exists(Path.Combine(directory.FullName, ".github")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class ResolverFixture : IDisposable
    {
        private ResolverFixture(string root, string binDirectory)
        {
            Root = root;
            BinDirectory = binDirectory;
        }

        public string Root { get; }
        public string BinDirectory { get; }

        public static ResolverFixture Create(string runHeadBranch = PrHeadRef, string runCreatedAt = "2026-09-09T21:57:32Z")
        {
            var root = Directory.CreateTempSubdirectory("webassistant-resolver-post-merge-").FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var fakeGh = Path.Combine(bin, "gh");
            var runs = $$"""{"workflow_runs":[{"id":101,"path":".github/workflows/ci.yml","event":"pull_request","status":"completed","conclusion":"success","head_sha":"{{PrHeadSha}}","head_branch":"{{runHeadBranch}}","created_at":"{{runCreatedAt}}","pull_requests":[]},{"id":102,"path":".github/workflows/repo-guard.yml","event":"pull_request","status":"completed","conclusion":"success","head_sha":"{{PrHeadSha}}","head_branch":"{{runHeadBranch}}","created_at":"{{runCreatedAt}}","pull_requests":[]}]}""";

            File.WriteAllText(fakeGh, $$"""
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == "api" ]] || exit 91
endpoint="${2:-}"
case "$endpoint" in
  "repos/test/repo/branches/main")
    printf '%s\n' '{"commit":{"sha":"{{SourceSha}}"}}' ;;
  "repos/test/repo/commits/{{SourceSha}}/pulls")
    printf '%s\n' '{"number":17,"created_at":"2026-09-09T19:41:36Z","merged_at":"2026-09-09T22:02:37Z","merge_commit_sha":"{{SourceSha}}","head":{"sha":"{{PrHeadSha}}","ref":"{{PrHeadRef}}"},"base":{"ref":"main","sha":"{{BaseSha}}"}}' | python3 -c 'import json,sys; print(json.dumps([json.load(sys.stdin)]))' ;;
  "repos/test/repo/actions/runs?head_sha={{PrHeadSha}}&event=pull_request&per_page=100")
    printf '%s\n' '{{runs}}' ;;
  "repos/test/repo/actions/runs/101/jobs?per_page=100")
    printf '%s\n' '{"jobs":[{"name":"ci-required","status":"completed","conclusion":"success"}]}' ;;
  "repos/test/repo/actions/runs/102/jobs?per_page=100")
    printf '%s\n' '{"jobs":[{"name":"repo-guard","status":"completed","conclusion":"success"}]}' ;;
  "repos/test/repo/contents/webassist/VERSION?ref={{SourceSha}}")
    printf '%s\n' '{"content":"MC4zLjIwCg=="}' ;;
  "repos/test/repo/contents/webassist/VERSION?ref={{BaseSha}}")
    printf '%s\n' '{"content":"MC4zLjE5Cg=="}' ;;
  *)
    printf 'unexpected endpoint: %s\n' "$endpoint" >&2
    exit 92 ;;
esac
""");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fakeGh,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return new ResolverFixture(root, bin);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
