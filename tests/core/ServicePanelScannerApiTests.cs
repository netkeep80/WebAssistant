using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ServicePanelScannerApiTests
{
    [Fact]
    public void ServicePanel_UsesOnlyUnifiedJsonScanRoute()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("/v1/scan", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/scan/feeder", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/scan/duplex", html, StringComparison.Ordinal);
        Assert.Contains("JSON.stringify", html, StringComparison.Ordinal);
        Assert.Contains("scannerId", html, StringComparison.Ordinal);
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
