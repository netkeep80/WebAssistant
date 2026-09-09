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
        Assert.Contains("source-appsettings", windows, StringComparison.Ordinal);
        Assert.Contains("generated-default", windows, StringComparison.Ordinal);

        Assert.Contains("src/WebAssistant/appsettings.json", linux, StringComparison.Ordinal);
        Assert.Contains("build/common/default-appsettings.json", linux, StringComparison.Ordinal);
        Assert.Contains("source-appsettings", linux, StringComparison.Ordinal);
        Assert.Contains("generated-default", linux, StringComparison.Ordinal);
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
    public void LinuxInstaller_DefaultStateDirectoriesMatchRuntimeDefaults()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runtime = WebAssistantRuntimeOptions.Load(new ConfigurationBuilder().Build());
        Assert.Equal("/var/log/webassistant", runtime.LogDirectory);
        Assert.Equal("/var/lib/webassistant", runtime.FileSystemRootDirectory);

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
    public void WindowsInstaller_PreservesPackageOwnedAppsettings()
    {
        var root = FindRepositoryRoot();
        var installPath = Path.Combine(
            root,
            "webassist",
            "install",
            "windows",
            "install.ps1");
        var install = File.ReadAllText(installPath);

        var copyIndex = install.IndexOf(
            "Copy-Item (Join-Path $sourceDirectory \"*\") $InstallDirectory -Recurse -Force",
            StringComparison.Ordinal);
        var preserveGuardIndex = install.IndexOf(
            "if (-not (Test-Path -LiteralPath $configFile))",
            StringComparison.Ordinal);
        var configWriteIndex = install.IndexOf(
            "Set-Content -Path $configFile",
            StringComparison.Ordinal);

        Assert.True(copyIndex >= 0, "Installer must copy package app before configuration handling.");
        Assert.True(
            preserveGuardIndex > copyIndex,
            "Installer must check for an already copied package appsettings.json before generating defaults.");
        Assert.True(
            configWriteIndex > preserveGuardIndex,
            "Default appsettings.json may only be generated inside the missing-config guard.");
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
