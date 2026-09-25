using Xunit;

namespace WebAssistant.CoreTests;

// Имя файла временно сохраняется как evidence-path current accepted v0.2.1.
// Candidate v0.3 этот legacy path больше не требует.
public sealed class RootedContainmentCompatibilityTests
{
    [Fact]
    public void ObsoleteRootedPathResolverSource_IsAbsent()
    {
        var repository = FindRepositoryRoot();

        Assert.False(File.Exists(Path.Combine(
            repository,
            "webassist",
            "src",
            "WebAssistant",
            "FileSystem",
            "RootedPathResolver.cs")));

        Assert.True(File.Exists(Path.Combine(
            repository,
            "webassist",
            "src",
            "WebAssistant",
            "FileSystem",
            "WindowsRootedFileSystem.cs")));

        Assert.True(File.Exists(Path.Combine(
            repository,
            "webassist",
            "src",
            "WebAssistant",
            "FileSystem",
            "LinuxRootedFileSystem.cs")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "repo-policy.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}
