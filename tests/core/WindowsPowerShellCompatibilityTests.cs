using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsPowerShellCompatibilityTests
{
    [Fact]
    public void ProductPowerShellScriptsUseUtf8BomForWindowsPowerShell51()
    {
        var repositoryRoot = FindRepositoryRoot();
        var productRoot = Path.Combine(repositoryRoot, "webassist");
        var scripts = new[]
        {
            Path.Combine(productRoot, "build", "windows", "package.ps1"),
            Path.Combine(productRoot, "install", "windows", "install.ps1"),
            Path.Combine(productRoot, "install", "windows", "uninstall.ps1")
        };

        foreach (var script in scripts)
        {
            Assert.True(File.Exists(script), $"PowerShell script not found: {script}");
            var bytes = File.ReadAllBytes(script);

            Assert.True(
                bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF,
                $"{script} must be UTF-8 with BOM so Windows PowerShell 5.1 does not parse UTF-8 Cyrillic text as the active ANSI code page.");
        }
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
