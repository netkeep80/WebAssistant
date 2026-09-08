using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ConfigurationOwnershipTests
{
    [Fact]
    public void PublicRepository_DoesNotOwnEnvironmentSpecificAppsettings()
    {
        var root = FindRepositoryRoot();
        var configPath = Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "appsettings.json");

        Assert.False(
            File.Exists(configPath),
            "Environment-specific appsettings.json must be owned by the consumer/deployment, not by the public WebAssistant repository.");
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
