using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LineEndingPolicyTests
{
    [Fact]
    public void DevelopmentAndExportRoots_ForceLfForShellScripts()
    {
        var root = FindRepositoryRoot();

        AssertForcesShellLf(Path.Combine(root, ".gitattributes"));
        AssertForcesShellLf(Path.Combine(root, "webassist", ".gitattributes"));
    }

    [Fact]
    public void CheckedOutShellScripts_DoNotContainCarriageReturns()
    {
        var root = FindRepositoryRoot();

        foreach (var path in Directory.EnumerateFiles(root, "*.sh", SearchOption.AllDirectories))
        {
            if (IsGeneratedPath(root, path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            Assert.DoesNotContain('\r', text);
        }
    }

    private static void AssertForcesShellLf(string path)
    {
        Assert.True(File.Exists(path), $"Отсутствует line-ending policy: {path}");
        var policy = File.ReadAllText(path);
        Assert.Contains("*.sh text eol=lf", policy, StringComparison.Ordinal);
    }

    private static bool IsGeneratedPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               relative.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);
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
