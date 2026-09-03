using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DependencyOwnershipTests
{
    [Fact]
    public void Product_UsesDistinctRepositoryOwnedFixedSdkIdentity()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "WebAssistant.csproj"));

        Assert.Contains("WebAssistant.NAPS2.Sdk", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference Include=\"NAPS2.Sdk\"", project, StringComparison.Ordinal);
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

        throw new InvalidOperationException("Не найден корень репозитория.");
    }
}
