using System.Xml.Linq;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class SystemServiceProductTests
{
    [Fact]
    public void LinuxPackage_ContainsHardenedSystemdLifecycleSurface()
    {
        var root = FindRepositoryRoot();
        var product = Path.Combine(root, "webassist");
        var projectPath = Path.Combine(product, "src", "WebAssistant", "WebAssistant.csproj");
        var programPath = Path.Combine(product, "src", "WebAssistant", "Program.cs");
        var packageScript = Path.Combine(product, "build", "linux", "package.sh");
        var installScript = Path.Combine(product, "install", "linux", "install.sh");
        var uninstallScript = Path.Combine(product, "install", "linux", "uninstall.sh");
        var unitFile = Path.Combine(product, "install", "linux", "webassist.service");
        var documentation = Path.Combine(product, "docs", "linux-service.md");
        var acceptance = Path.Combine(root, "tests", "linux-systemd", "run-systemd-acceptance.sh");
        var workflow = Path.Combine(root, ".github", "workflows", "linux-systemd.yml");

        Assert.True(File.Exists(packageScript));
        Assert.True(File.Exists(installScript));
        Assert.True(File.Exists(uninstallScript));
        Assert.True(File.Exists(unitFile));
        Assert.True(File.Exists(documentation));
        Assert.True(File.Exists(acceptance));
        Assert.True(File.Exists(workflow));

        var project = XDocument.Load(projectPath);
        var package = project.Descendants("PackageReference").SingleOrDefault(element =>
            string.Equals(
                element.Attribute("Include")?.Value,
                "Microsoft.Extensions.Hosting.Systemd",
                StringComparison.Ordinal));
        Assert.NotNull(package);
        Assert.Equal("10.0.11", package.Attribute("Version")?.Value);

        var program = File.ReadAllText(programPath);
        Assert.Contains("UseSystemd", program, StringComparison.Ordinal);

        var packageText = File.ReadAllText(packageScript);
        Assert.Contains("linux-x64", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--self-contained true", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".rpm", packageText, StringComparison.OrdinalIgnoreCase);

        var install = File.ReadAllText(installScript);
        Assert.Contains("apt-get", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("libicu74", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("systemctl", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enable webassist.service", install, StringComparison.OrdinalIgnoreCase);

        var unit = File.ReadAllText(unitFile);
        Assert.Contains("Type=notify", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/webassist/WebAssistant", unit, StringComparison.Ordinal);
        Assert.Contains("User=webassist", unit, StringComparison.Ordinal);
        Assert.Contains("Group=webassist", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", unit, StringComparison.Ordinal);
        Assert.Contains("PrivateTmp=true", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);

        var uninstall = File.ReadAllText(uninstallScript);
        Assert.Contains("disable --now webassist.service", uninstall, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("userdel webassist", uninstall, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groupdel webassist", uninstall, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPackage_ContainsRealServiceLifecycleSurface()
    {
        var root = FindRepositoryRoot();
        var product = Path.Combine(root, "webassist");
        var projectPath = Path.Combine(product, "src", "WebAssistant", "WebAssistant.csproj");
        var programPath = Path.Combine(product, "src", "WebAssistant", "Program.cs");
        var packageScript = Path.Combine(product, "build", "windows", "package.ps1");
        var packageBatch = Path.Combine(product, "build", "windows", "package.bat");
        var installScript = Path.Combine(product, "install", "windows", "install.ps1");
        var installBatch = Path.Combine(product, "install", "windows", "install.bat");
        var uninstallScript = Path.Combine(product, "install", "windows", "uninstall.ps1");
        var uninstallBatch = Path.Combine(product, "install", "windows", "uninstall.bat");
        var documentation = Path.Combine(product, "docs", "windows-service.md");
        var acceptance = Path.Combine(root, "tests", "windows-service", "run-service-acceptance.ps1");
        var workflow = Path.Combine(root, ".github", "workflows", "windows-service.yml");

        Assert.True(File.Exists(packageScript));
        Assert.True(File.Exists(packageBatch));
        Assert.True(File.Exists(installScript));
        Assert.True(File.Exists(installBatch));
        Assert.True(File.Exists(uninstallScript));
        Assert.True(File.Exists(uninstallBatch));
        Assert.True(File.Exists(documentation));
        Assert.True(File.Exists(acceptance));
        Assert.True(File.Exists(workflow));

        var project = XDocument.Load(projectPath);
        var package = project.Descendants("PackageReference").SingleOrDefault(element =>
            string.Equals(
                element.Attribute("Include")?.Value,
                "Microsoft.Extensions.Hosting.WindowsServices",
                StringComparison.Ordinal));
        Assert.NotNull(package);
        Assert.Equal("10.0.11", package.Attribute("Version")?.Value);

        var program = File.ReadAllText(programPath);
        Assert.Contains("AddWindowsService", program, StringComparison.Ordinal);
        Assert.Contains("ServiceName = \"WebAssistant\"", program, StringComparison.Ordinal);

        var packageText = File.ReadAllText(packageScript);
        Assert.Contains("win-x64", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--self-contained true", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install.ps1", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uninstall.ps1", packageText, StringComparison.OrdinalIgnoreCase);

        var packageBatText = File.ReadAllText(packageBatch);
        Assert.Contains("%~dp0", packageBatText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet --list-sdks", packageBatText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget install", packageBatText, StringComparison.OrdinalIgnoreCase);

        var install = File.ReadAllText(installScript);
        Assert.Contains("sc.exe create", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start= auto", install, StringComparison.OrdinalIgnoreCase);

        var uninstall = File.ReadAllText(uninstallScript);
        Assert.Contains("sc.exe delete", uninstall, StringComparison.OrdinalIgnoreCase);
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
