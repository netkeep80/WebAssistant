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

        Assert.Contains("id=\"scanner-mode\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-settings-controls\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"scanner-info\"", html, StringComparison.Ordinal);
        Assert.Contains("/v1/scanner-settings/schema", html, StringComparison.Ordinal);
        Assert.Contains("/settings", html, StringComparison.Ordinal);
        Assert.Contains("Object.entries(scannerSchema.fields", html, StringComparison.Ordinal);
        Assert.Contains("settingControlId(fieldName)", html, StringComparison.Ordinal);
        Assert.Contains("select.dataset.settingName=fieldName", html, StringComparison.Ordinal);

        Assert.DoesNotContain("id=\"scan-feeder\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"scan-duplex\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicePanel_HasOneScanActionAndCapabilityDrivenModeSelector()
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
        Assert.Contains("for(const mode of scannerProjection.modes", html, StringComparison.Ordinal);
        Assert.Contains("source:mode.source", html, StringComparison.Ordinal);
        Assert.Contains("duplex:Boolean(mode.duplex)", html, StringComparison.Ordinal);
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
