using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsInstallerPresentationContractTests
{
    [Fact]
    public void Candidate_RequiresRussianWindowsPresentationAndRepositoryOwnedIcon()
    {
        var contract = ReadRequired("contracts/webassistant-contract-v0.3.json");
        var conformance = ReadRequired("contracts/webassistant-conformance-v0.3.json");

        Assert.Contains("WA-WIN-INSTALL-001", contract, StringComparison.Ordinal);
        Assert.Contains("WixStandardBootstrapperApplication", contract, StringComparison.Ordinal);
        Assert.Contains("рус", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repository-owned", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WA-C-WINDOWS-PRESENTATION-001", conformance, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBundle_UsesExplicitRussianLocalizationAndGeneratedBranding()
    {
        var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
        var project = ReadRequired("webassist/build/windows/installer/WebAssistant.Bundle.wixproj");

        Assert.Contains("LocalizationFile=\"$(var.LocalizationFile)\"", bundle, StringComparison.Ordinal);
        Assert.Contains("LogoFile=\"$(var.LogoFile)\"", bundle, StringComparison.Ordinal);
        Assert.Contains("IconSourceFile=\"$(var.BundleIconPath)\"", bundle, StringComparison.Ordinal);
        Assert.Contains("LocalizationFile=$(LocalizationFile)", project, StringComparison.Ordinal);
        Assert.Contains("LogoFile=$(LogoFile)", project, StringComparison.Ordinal);
        Assert.Contains("BundleIconPath=$(BundleIconPath)", project, StringComparison.Ordinal);
    }

    [Fact]
    public void StableTechnicalInstallerIdsRemainUnchanged()
    {
        var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
        var package = ReadRequired("webassist/build/windows/installer/Package.wxs");

        Assert.Contains("Id=\"netkeep80.WebAssistant.Bundle\"", bundle, StringComparison.Ordinal);
        Assert.Contains("Id=\"netkeep80.WebAssistant\"", package, StringComparison.Ordinal);
        Assert.Contains("Name=\"WebAssistant\"", package, StringComparison.Ordinal);
        Assert.Contains("WebAssistant.exe", package, StringComparison.Ordinal);
    }

    private static string ReadRequired(string relativePath)
    {
        var path = ToFullPath(relativePath);
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string ToFullPath(string relativePath) =>
        Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

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
