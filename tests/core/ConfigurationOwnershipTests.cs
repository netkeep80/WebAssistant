using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WebAssistant.Runtime;
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

    [Fact]
    public void CanonicalProducers_SelectSourceAppsettingsOrSharedSafeDefaultAtPackageTime()
    {
        var windows = ReadRequired("webassist/build/windows/package.ps1");
        var linux = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("src/WebAssistant/appsettings.json", windows, StringComparison.Ordinal);
        Assert.Contains("build/common/default-appsettings.json", windows, StringComparison.Ordinal);
        Assert.Contains("build/common/appsettings.example.json", windows, StringComparison.Ordinal);
        Assert.Contains("source-appsettings", windows, StringComparison.Ordinal);
        Assert.Contains("generated-default", windows, StringComparison.Ordinal);

        Assert.Contains("src/WebAssistant/appsettings.json", linux, StringComparison.Ordinal);
        Assert.Contains("build/common/default-appsettings.json", linux, StringComparison.Ordinal);
        Assert.Contains("source-appsettings", linux, StringComparison.Ordinal);
        Assert.Contains("generated-default", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSafeDefault_DoesNotInventFileSystemAuthority()
    {
        using var document = JsonDocument.Parse(
            ReadRequired("webassist/build/common/default-appsettings.json"));
        var fileSystem = document.RootElement
            .GetProperty("WebAssistant")
            .GetProperty("FileSystem");

        Assert.Equal(JsonValueKind.Object, fileSystem.ValueKind);
        Assert.Empty(fileSystem.EnumerateObject());
    }

    [Fact]
    public void WindowsWixInstaller_InstallsPersistentConfigOutsideProgramFilesAndRefreshesExample()
    {
        var package = ReadRequired("webassist/build/windows/installer/Package.wxs");

        Assert.Contains(
            "<Exclude Files=\"$(var.PayloadRoot)\\appsettings.json\" />",
            package,
            StringComparison.Ordinal);
        Assert.Contains("Id=\"WebAssistantConfigurationComponent\"", package, StringComparison.Ordinal);
        Assert.Contains(
            "Id=\"WebAssistantConfigurationComponent\"\n        Directory=\"WEBAssistantConfigDirectory\"\n        Guid=\"*\"\n        Permanent=\"yes\"\n        NeverOverwrite=\"yes\"",
            package,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Id=\"WebAssistantConfigurationComponent\"\n        Directory=\"INSTALLFOLDER\"",
            package,
            StringComparison.Ordinal);
        Assert.Contains("Id=\"WebAssistantConfigurationFile\"", package, StringComparison.Ordinal);
        Assert.Contains(
            "Source=\"$(var.PayloadRoot)\\appsettings.json\"",
            package,
            StringComparison.Ordinal);
        Assert.Contains("WEBAssistantConfigDirectory", package, StringComparison.Ordinal);
        Assert.Contains("Name=\"config\"", package, StringComparison.Ordinal);
        Assert.Contains("Id=\"WebAssistantExampleConfigurationComponent\"", package, StringComparison.Ordinal);
        Assert.Contains(
            "Source=\"$(var.PayloadRoot)\\appsettings.example.json\"",
            package,
            StringComparison.Ordinal);
        Assert.Contains("Name=\"appsettings.example.json\"", package, StringComparison.Ordinal);
        Assert.Contains("On=\"install\"", package, StringComparison.Ordinal);
        Assert.Contains("Permanent=\"yes\"", package, StringComparison.Ordinal);
        Assert.Contains("KeyPath=\"yes\"", package, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsWixInstaller_RemovesHistoricalProductAfterCandidateComponentsAreInstalled()
    {
        var package = ReadRequired("webassist/build/windows/installer/Package.wxs");

        Assert.Contains("<MajorUpgrade", package, StringComparison.Ordinal);
        Assert.Contains("Schedule=\"afterInstallExecute\"", package, StringComparison.Ordinal);
        Assert.Contains(
            "DowngradeErrorMessage=\"!(loc.WixDowngradePreventedMessage)\"",
            package,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AllowDowngrades=\"yes\"", package, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxInstaller_RequiresPackagedAppsettingsAndCopiesPayloadWithoutGeneratingDefaults()
    {
        var install = ReadRequired("webassist/install/linux/install.sh");

        Assert.Contains("source_config=\"$source_app/appsettings.json\"", install, StringComparison.Ordinal);
        Assert.Contains("[[ -f \"$source_config\" ]]", install, StringComparison.Ordinal);
        Assert.Contains("cp -a -- \"$source_app\"/. \"$install_dir\"/", install, StringComparison.Ordinal);

        Assert.DoesNotContain("cat > \"$config_file\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("cat >\"$config_file\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("<<'JSON'", install, StringComparison.Ordinal);
        Assert.DoesNotContain("<<\"JSON\"", install, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxInstaller_DefaultServiceDirectoriesRemainStable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runtime = WebAssistantRuntimeOptions.Load(new ConfigurationBuilder().Build());
        Assert.Equal("/var/log/webassistant", runtime.LogDirectory);

        var install = ReadRequired("webassist/install/linux/install.sh");
        var uninstall = ReadRequired("webassist/install/linux/uninstall.sh");
        var acceptance = ReadRequired("tests/linux-systemd/run-systemd-acceptance.sh");

        Assert.Contains("log_dir=\"/var/log/webassistant\"", install, StringComparison.Ordinal);
        Assert.Contains("data_dir=\"/var/lib/webassistant\"", install, StringComparison.Ordinal);
        Assert.Contains("/var/log/webassistant /var/lib/webassistant", uninstall, StringComparison.Ordinal);
        Assert.Contains("LOG_DIR=\"/var/log/webassistant\"", acceptance, StringComparison.Ordinal);
        Assert.Contains("DATA_DIR=\"/var/lib/webassistant\"", acceptance, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstaller_UsesPersistentConfigAndExplicitPurgeSwitch()
    {
        var install = ReadRequired("webassist/install/windows/install.ps1");
        var uninstall = ReadRequired("webassist/install/windows/uninstall.ps1");

        Assert.Contains("$configDirectory = Join-Path $stateRoot \"config\"", install, StringComparison.Ordinal);
        Assert.Contains("$configFile = Join-Path $configDirectory \"appsettings.json\"", install, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $legacyConfigFile -Destination $configFile", install, StringComparison.Ordinal);
        Assert.Contains("persistent appsettings.json", install, StringComparison.Ordinal);
        Assert.Contains("appsettings.example.json", install, StringComparison.Ordinal);

        Assert.Contains("[switch]$PurgeConfig", uninstall, StringComparison.Ordinal);
        Assert.Contains("persistent configuration", uninstall, StringComparison.Ordinal);
        Assert.Contains("$runtimeDataDirectory", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item $dataRoot -Recurse -Force", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void FullExample_CoversCurrentConfigurationSurface()
    {
        var example = ReadRequired("webassist/build/common/appsettings.example.json");
        var normalized = NativeWindowsPathJsonPreprocessor.Normalize(example, out _);

        using var document = JsonDocument.Parse(normalized, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("Logging", out _));
        var webAssistant = root.GetProperty("WebAssistant");
        Assert.True(webAssistant.TryGetProperty("Port", out _));
        Assert.True(webAssistant.TryGetProperty("LogDirectory", out _));
        Assert.True(webAssistant.TryGetProperty("Cors", out _));
        var fileSystem = webAssistant.GetProperty("FileSystem");
        Assert.True(fileSystem.EnumerateObject().Count() >= 4);

        Assert.Contains("C:/Documents", example, StringComparison.Ordinal);
        Assert.Contains(@"D:\Archive", example, StringComparison.Ordinal);
        Assert.Contains(@"C:\\Program Files\\Example App", example, StringComparison.Ordinal);
        Assert.Contains(@"\\server\share\import", example, StringComparison.Ordinal);
    }

    private static string ReadRequired(string relativePath)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
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
