using System.Diagnostics;
using System.Text;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ReleaseResolverPostMergeTests
{
    private const string SourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PrHeadSha = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void AcceptedMainResolver_AcceptsPostMergeWorkflowRunsWithEmptyPullRequests()
    {
        var root = FindRepositoryRoot();
        var temp = Directory.CreateTempSubdirectory("webassistant-resolver-post-merge-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(temp.FullName, "bin")).FullName;
            var fakeGh = Path.Combine(bin, "gh");
            File.WriteAllText(fakeGh, """
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == "api" ]] || exit 91
endpoint="${2:-}"
case "$endpoint" in
  "repos/test/repo/branches/main")
    printf '{"commit":{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}\n' ;;
  "repos/test/repo/commits/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pulls")
    printf '[{"number":17,"merged_at":"2026-09-09T22:02:37Z","merge_commit_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","head":{"sha":"cccccccccccccccccccccccccccccccccccccccc"},"base":{"ref":"main","sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]\n' ;;
  "repos/test/repo/actions/runs?head_sha=cccccccccccccccccccccccccccccccccccccccc&event=pull_request&per_page=100")
    printf '{"workflow_runs":[{"id":101,"path":".github/workflows/ci.yml","event":"pull_request","status":"completed","conclusion":"success","head_sha":"cccccccccccccccccccccccccccccccccccccccc","pull_requests":[]},{"id":102,"path":".github/workflows/repo-guard.yml","event":"pull_request","status":"completed","conclusion":"success","head_sha":"cccccccccccccccccccccccccccccccccccccccc","pull_requests":[]}]}\n' ;;
  "repos/test/repo/actions/runs/101/jobs?per_page=100")
    printf '{"jobs":[{"name":"ci-required","status":"completed","conclusion":"success"}]}\n' ;;
  "repos/test/repo/actions/runs/102/jobs?per_page=100")
    printf '{"jobs":[{"name":"repo-guard","status":"completed","conclusion":"success"}]}\n' ;;
  "repos/test/repo/contents/webassist/VERSION?ref=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    printf '{"content":"MC4zLjE5Cg=="}\n' ;;
  "repos/test/repo/contents/webassist/VERSION?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
    printf '{"content":"MC4zLjE4Cg=="}\n' ;;
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

            var script = Path.Combine(root, "ci", "release", "resolve-accepted-main.sh");
            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(SourceSha);
            startInfo.Environment["GH_REPO"] = "test/repo";
            startInfo.Environment["PATH"] = bin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить accepted-main resolver.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"Post-merge GitHub shape must remain valid acceptance evidence. stderr: {error}\nstdout: {output}");
            Assert.Contains(SourceSha, output, StringComparison.Ordinal);
            Assert.Contains(PrHeadSha, output, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
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
}
