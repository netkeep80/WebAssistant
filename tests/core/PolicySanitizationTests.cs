using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class PolicySanitizationTests
{
    [Fact]
    public void ProtectedIdentities_AreAbsentWhileExistingPolicyBlockersRemainSemanticEquals()
    {
        var root = FindRepositoryRoot();
        var policyPath = Path.Combine(root, "repo-policy.json");

        using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
        var rule = policy.RootElement
            .GetProperty("content_rules")
            .EnumerateArray()
            .Single(element => string.Equals(
                element.GetProperty("id").GetString(),
                "no-legacy-or-environment-identities",
                StringComparison.Ordinal));

        var patterns = rule
            .GetProperty("forbid_regex")
            .EnumerateArray()
            .Select(element => Assert.IsType<string>(element.GetString()))
            .ToArray();

        foreach (var expectedPattern in PreservedPolicyPatterns())
        {
            Assert.Contains(expectedPattern, patterns);
        }

        foreach (var relativePath in GetTrackedFiles(root))
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath) || !LooksLikeText(fullPath))
            {
                continue;
            }

            var text = File.ReadAllText(fullPath);
            foreach (var identity in ProtectedTrackedIdentities())
            {
                Assert.DoesNotContain(identity, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static IReadOnlyList<string> PreservedPolicyPatterns() =>
    [
        string.Concat("Tri", "umf"),
        string.Concat("dep", "fin", "\\.", "nnov", "\\.", "ru")
    ];

    private static IReadOnlyList<string> ProtectedTrackedIdentities() =>
    [
        string.Concat("Tri", "umf"),
        string.Concat("три", "умф"),
        string.Concat("департамент", " ", "финансов"),
        string.Concat("dep", "fin", ".", "nnov", ".", "ru")
    ];

    private static IReadOnlyList<string> GetTrackedFiles(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить git ls-files.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"git ls-files завершился с ошибкой: {error}");

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
    }

    private static bool LooksLikeText(string path)
    {
        using var stream = File.OpenRead(path);
        var length = checked((int)Math.Min(stream.Length, 4096L));
        var buffer = new byte[length];
        var read = stream.Read(buffer, 0, buffer.Length);
        return !buffer.AsSpan(0, read).Contains((byte)0);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
