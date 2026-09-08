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
