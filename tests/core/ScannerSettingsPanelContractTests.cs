using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerSettingsPanelContractTests
{
    [Fact]
    public void ServicePanel_RendersScannerSettingsFromPublicCapabilitiesContract()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("scanner-mode", html, StringComparison.Ordinal);
        Assert.Contains("scanner-dpi", html, StringComparison.Ordinal);
        Assert.Contains("scanner-color-mode", html, StringComparison.Ordinal);
        Assert.Contains("scanner-paper-size", html, StringComparison.Ordinal);
        Assert.Contains("scanner-info", html, StringComparison.Ordinal);
        Assert.Contains("/v1/scanner-settings/schema", html, StringComparison.Ordinal);
        Assert.Contains("/settings", html, StringComparison.Ordinal);

        Assert.DoesNotContain("scan-feeder", html, StringComparison.Ordinal);
        Assert.DoesNotContain("scan-duplex", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicePanel_HasOneScanActionAndModeSelector()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("id=\"scan-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-mode\"", html, StringComparison.Ordinal);
        Assert.Contains("Информация о сканере", html, StringComparison.Ordinal);
        Assert.Contains("Сканировать", html, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "webassist",
                    "src",
                    "WebAssistant",
                    "WebAssistant.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}
