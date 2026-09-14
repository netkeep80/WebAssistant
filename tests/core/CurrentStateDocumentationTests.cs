using Xunit;

namespace WebAssistant.CoreTests;

public sealed class CurrentStateDocumentationTests
{
    [Fact]
    public void RootReadme_AcknowledgesCurrentFilesystemCandidateSurface()
    {
        var readme = ReadRepositoryFile("README.md");

        Assert.Contains("/v1/filesystem/list", readme, StringComparison.Ordinal);
        Assert.Contains("candidate v0.3", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "browser-facing filesystem routes в текущем accepted baseline отсутствуют",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductReadme_DescribesCurrentWindowsScannerDiscovery()
    {
        var readme = ReadRepositoryFile("webassist/README.md");

        Assert.Contains("WIA", readme, StringComparison.Ordinal);
        Assert.Contains("TWAIN", readme, StringComparison.Ordinal);
        Assert.Contains("перечисляются независимо", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "сначала используется WIA; переход на TWAIN происходит только если WIA не вернул ни одного устройства",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductReadme_DescribesExactAutoFallbackBoundary()
    {
        var readme = ReadRepositoryFile("webassist/README.md");

        Assert.Contains("явно заданный", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не имеет скрытого fallback", readme, StringComparison.Ordinal);
        Assert.Contains("auto", readme, StringComparison.Ordinal);
        Assert.Contains("один раз", readme, StringComparison.Ordinal);
        Assert.Contains("flatbed", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductReadme_DescribesCurrentFilesystemCapability()
    {
        var readme = ReadRepositoryFile("webassist/README.md");

        Assert.Contains("/v1/filesystem/list", readme, StringComparison.Ordinal);
        Assert.Contains("/filesystem.html", readme, StringComparison.Ordinal);
        Assert.Contains("RootDirectory", readme, StringComparison.Ordinal);
        Assert.Contains("no-replace", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "В текущей версии browser-facing filesystem endpoints отсутствуют",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApiDocumentation_DescribesFilesystemBrowserClient()
    {
        var api = ReadRepositoryFile("webassist/docs/api.md");

        Assert.Contains("/filesystem.html", api, StringComparison.Ordinal);
        Assert.Contains("public filesystem API", api, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceDocumentation_UsesEffectiveInstallerBasenamePatterns()
    {
        var windows = ReadRepositoryFile("webassist/docs/windows-service.md");
        var linux = ReadRepositoryFile("webassist/docs/linux-service.md");

        Assert.Contains("<installerBaseName>-win-x64-<VERSION>.exe", windows, StringComparison.Ordinal);
        Assert.Contains("public default", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebAssistant", windows, StringComparison.Ordinal);

        Assert.Contains("<installerBaseName>-linux-x64-<VERSION>.zip", linux, StringComparison.Ordinal);
        Assert.Contains("public default", linux, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebAssistant", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceDocumentation_IdentifiesDefaultFilesystemRoots()
    {
        var windows = ReadRepositoryFile("webassist/docs/windows-service.md");
        var linux = ReadRepositoryFile("webassist/docs/linux-service.md");

        Assert.Contains("WebAssistant:FileSystem:RootDirectory", windows, StringComparison.Ordinal);
        Assert.Contains("%ProgramData%\\WebAssistant\\data", windows, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:FileSystem:RootDirectory", linux, StringComparison.Ordinal);
        Assert.Contains("/var/lib/webassistant", linux, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
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
