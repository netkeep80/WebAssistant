using Xunit;

namespace WebAssistant.CoreTests;

public sealed class CurrentStateDocumentationTests
{
    [Fact]
    public void RootReadme_AcknowledgesCurrentFilesystemCandidateSurface()
    {
        var readme = ReadRepositoryFile("README.md");

        Assert.Contains("/v1/filesystem/list", readme, StringComparison.Ordinal);
        Assert.Contains("v0.3", readme, StringComparison.Ordinal);
        Assert.Contains("v0.2.1", readme, StringComparison.Ordinal);
        Assert.Contains("остаётся кандидатом", readme, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("logicalRootName", readme, StringComparison.Ordinal);
        Assert.Contains("no-replace", readme, StringComparison.Ordinal);
        Assert.Contains("только `GET` и `POST`", readme, StringComparison.Ordinal);
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
        Assert.Contains("## Файловый обмен", api, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiDocumentation_DescribesCurrentFilesystemUiAndIdentityBoundaries()
    {
        var api = ReadRepositoryFile("webassist/docs/api.md");

        Assert.Contains(
            "LEFT и RIGHT имеют независимое состояние",
            api,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Выбранный логический корень общий для обеих панелей",
            api,
            StringComparison.Ordinal);
        Assert.Contains("Действия →", api, StringComparison.Ordinal);
        Assert.Contains("Действия ←", api, StringComparison.Ordinal);
        Assert.Contains(
            "требуют `Content-Type: application/json`",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "`scannerId` остаётся тем же, пока неизменны `backend` и точный `nativeId`",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceDocumentation_UsesEffectiveInstallerBasenamePatterns()
    {
        var windows = ReadRepositoryFile("webassist/docs/windows-service.md");
        var linux = ReadRepositoryFile("webassist/docs/linux-service.md");

        Assert.Contains("<installerBaseName>-win-x64-<VERSION>.exe", windows, StringComparison.Ordinal);
        Assert.Contains("публичном значении по умолчанию", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebAssistant", windows, StringComparison.Ordinal);

        Assert.Contains("<installerBaseName>-linux-x64-<VERSION>.zip", linux, StringComparison.Ordinal);
        Assert.Contains("публичном значении по умолчанию", linux, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ConfigurationDocumentation_DescribesActualRootDirectoryCompatibilityBoundary()
    {
        var configuration = ReadRepositoryFile("webassist/docs/configuration.md");
        var api = ReadRepositoryFile("webassist/docs/api.md");
        var windows = ReadRepositoryFile("webassist/docs/windows-service.md");
        var linux = ReadRepositoryFile("webassist/docs/linux-service.md");

        foreach (var document in new[] { configuration, api, windows, linux })
        {
            Assert.Contains(
                "обычный логический корень",
                document,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "RootDirectory` больше не используется файловой подсистемой",
                document,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "RootDirectory` больше не является действующей конфигурацией",
                document,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CanonicalHumanDocumentation_DoesNotUseKnownMixedLanguageNarrative()
    {
        var forbiddenByFile = new Dictionary<string, string[]>
        {
            ["README.md"] =
            [
                "локальная machine-wide служба",
                "Текущий scanner module",
                "Canonical product version",
                "## Repository governance"
            ],
            ["webassist/README.md"] =
            [
                "persisted source of truth for product version",
                "## Build-time product metadata",
                "Текущий scanner module",
                "## Runtime configuration и package-time ownership"
            ],
            ["webassist/docs/api.md"] =
            [
                "Текущая major version",
                "Machine endpoints",
                "Canonical machine-readable schema",
                "Единственный acquisition endpoint"
            ],
            ["webassist/docs/windows-service.md"] =
            [
                "## Canonical artifact",
                "## Package-owned configuration",
                "## Runtime state"
            ],
            ["webassist/docs/linux-service.md"] =
            [
                "## Canonical artifact",
                "## Package-owned configuration",
                "## Evidence boundary"
            ],
            ["webassist/docs/installation-guide.md"] =
            [
                "source commit",
                "canonical distribution artifacts",
                "release metadata"
            ],
            ["webassist/vendor/naps2/README.md"] =
            [
                "# Fixed NAPS2 SDK provenance",
                "Current committed package",
                "The .3 package"
            ]
        };

        foreach (var (path, forbiddenPhrases) in forbiddenByFile)
        {
            var content = ReadRepositoryFile(path);
            foreach (var phrase in forbiddenPhrases)
            {
                Assert.DoesNotContain(phrase, content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RootReadme_MarksDatedDevelopmentPlansAsHistoricalNonNormativeRecords()
    {
        var readme = ReadRepositoryFile("README.md");

        Assert.Contains("docs/superpowers", readme, StringComparison.Ordinal);
        Assert.Contains("исторические", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не являются нормативным описанием текущего продукта", readme, StringComparison.OrdinalIgnoreCase);
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
