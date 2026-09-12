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
        var runtimeDependencyScript = Path.Combine(product, "install", "linux", "runtime-dependencies.sh");
        var uninstallScript = Path.Combine(product, "install", "linux", "uninstall.sh");
        var unitFile = Path.Combine(product, "install", "linux", "webassist.service");
        var documentation = Path.Combine(product, "docs", "linux-service.md");
        var acceptance = Path.Combine(root, "tests", "linux-systemd", "run-systemd-acceptance.sh");
        var workflow = Path.Combine(root, ".github", "workflows", "linux-systemd.yml");

        Assert.True(File.Exists(packageScript));
        Assert.True(File.Exists(installScript));
        Assert.True(File.Exists(runtimeDependencyScript));
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
        Assert.Contains("runtime-dependencies.sh", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".rpm", packageText, StringComparison.OrdinalIgnoreCase);

        var install = File.ReadAllText(installScript);
        Assert.DoesNotContain("libicu74", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime-dependencies.sh", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ensure_webassistant_runtime_dependencies", install, StringComparison.Ordinal);
        Assert.Contains("systemctl", install, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enable webassist.service", install, StringComparison.OrdinalIgnoreCase);

        var runtimeDependencies = File.ReadAllText(runtimeDependencyScript);
        Assert.Contains("apt-get", runtimeDependencies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("libicu74", runtimeDependencies, StringComparison.OrdinalIgnoreCase);

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
        var installerRoot = Path.Combine(product, "build", "windows", "installer");
        var packageProject = Path.Combine(installerRoot, "WebAssistant.Package.wixproj");
        var packageDefinition = Path.Combine(installerRoot, "Package.wxs");
        var bundleProject = Path.Combine(installerRoot, "WebAssistant.Bundle.wixproj");
        var bundleDefinition = Path.Combine(installerRoot, "Bundle.wxs");
        var installScript = Path.Combine(product, "install", "windows", "install.ps1");
        var installBatch = Path.Combine(product, "install", "windows", "install.bat");
        var uninstallScript = Path.Combine(product, "install", "windows", "uninstall.ps1");
        var uninstallBatch = Path.Combine(product, "install", "windows", "uninstall.bat");
        var documentation = Path.Combine(product, "docs", "windows-service.md");
        var acceptance = Path.Combine(root, "tests", "windows-service", "run-service-acceptance.ps1");
        var upgradeAcceptance = Path.Combine(root, "tests", "windows-service", "run-upgrade-acceptance.ps1");
        var workflow = Path.Combine(root, ".github", "workflows", "windows-service.yml");

        Assert.True(File.Exists(packageScript));
        Assert.True(File.Exists(packageBatch));
        Assert.True(File.Exists(packageProject));
        Assert.True(File.Exists(packageDefinition));
        Assert.True(File.Exists(bundleProject));
        Assert.True(File.Exists(bundleDefinition));

        // Accepted v0.2 still requires these legacy evidence paths. They are no longer
        // the canonical installer implementation surface and must not be packaged.
        Assert.True(File.Exists(installScript));
        Assert.True(File.Exists(installBatch));
        Assert.True(File.Exists(uninstallScript));
        Assert.True(File.Exists(uninstallBatch));
        Assert.True(File.Exists(documentation));
        Assert.True(File.Exists(acceptance));
        Assert.True(File.Exists(upgradeAcceptance));
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

        var windowsBlockStart = program.IndexOf("if (OperatingSystem.IsWindows())", StringComparison.Ordinal);
        var linuxBlockStart = program.IndexOf("else if (OperatingSystem.IsLinux())", StringComparison.Ordinal);
        Assert.True(windowsBlockStart >= 0 && linuxBlockStart > windowsBlockStart);
        var windowsRegistrationBlock = program[windowsBlockStart..linuxBlockStart];
        Assert.Contains("AddSingleton<WindowsScanAdapterHolder>()", windowsRegistrationBlock, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<WindowsScanAdapterHolder>().GetOrCreate()", windowsRegistrationBlock, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<WindowsScannerShutdownHostedService>()", windowsRegistrationBlock, StringComparison.Ordinal);
        Assert.Contains("Configure<HostOptions>", windowsRegistrationBlock, StringComparison.Ordinal);
        Assert.Contains("ShutdownTimeout = TimeSpan.FromSeconds(20)", windowsRegistrationBlock, StringComparison.Ordinal);

        var packageText = File.ReadAllText(packageScript);
        Assert.Contains("win-x64", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--self-contained true", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebAssistant.Package.wixproj", packageText, StringComparison.Ordinal);
        Assert.Contains("WebAssistant.Bundle.wixproj", packageText, StringComparison.Ordinal);
        Assert.Contains("$artifactName = \"$installerBaseName-win-x64-$version.exe\"", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("install.ps1", packageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uninstall.ps1", packageText, StringComparison.OrdinalIgnoreCase);

        var packageProjectText = File.ReadAllText(packageProject);
        var bundleProjectText = File.ReadAllText(bundleProject);
        Assert.Contains("WixToolset.Sdk/7.0.0", packageProjectText, StringComparison.Ordinal);
        Assert.Contains("WixToolset.Sdk/7.0.0", bundleProjectText, StringComparison.Ordinal);
        Assert.Contains("WixToolset.BootstrapperApplications.wixext", bundleProjectText, StringComparison.Ordinal);
        Assert.Contains("Version=\"7.0.0\"", bundleProjectText, StringComparison.Ordinal);

        var wixPackage = XDocument.Load(packageDefinition);
        var wixPackageElement = wixPackage.Descendants().Single(element => element.Name.LocalName == "Package");
        Assert.Equal("$(var.ProductVersion)", wixPackageElement.Attribute("Version")?.Value);
        Assert.Equal("perMachine", wixPackageElement.Attribute("Scope")?.Value);

        var programFilesDirectory = wixPackage.Descendants().Single(element =>
            element.Name.LocalName == "StandardDirectory" &&
            element.Attribute("Id")?.Value == "ProgramFiles64Folder");
        Assert.NotNull(programFilesDirectory);

        var serviceInstall = wixPackage.Descendants().Single(element => element.Name.LocalName == "ServiceInstall");
        Assert.Equal("WebAssistant", serviceInstall.Attribute("Name")?.Value);
        Assert.Equal("auto", serviceInstall.Attribute("Start")?.Value);
        Assert.Equal("ownProcess", serviceInstall.Attribute("Type")?.Value);

        var serviceControl = wixPackage.Descendants().Single(element => element.Name.LocalName == "ServiceControl");
        Assert.Equal("WebAssistant", serviceControl.Attribute("Name")?.Value);
        Assert.Equal("install", serviceControl.Attribute("Start")?.Value);
        Assert.Equal("both", serviceControl.Attribute("Stop")?.Value);
        Assert.Equal("uninstall", serviceControl.Attribute("Remove")?.Value);

        var wixBundle = XDocument.Load(bundleDefinition);
        var bundle = wixBundle.Descendants().Single(element => element.Name.LocalName == "Bundle");
        Assert.Equal("$(var.ProductVersion)", bundle.Attribute("Version")?.Value);
        Assert.Equal("yes", bundle.Attribute("Compressed")?.Value);

        var msiPackage = wixBundle.Descendants().Single(element => element.Name.LocalName == "MsiPackage");
        Assert.Equal("yes", msiPackage.Attribute("ForcePerMachine")?.Value);
        Assert.Equal("no", msiPackage.Attribute("Visible")?.Value);
        Assert.Equal("yes", msiPackage.Attribute("Vital")?.Value);

        var packageBatText = File.ReadAllText(packageBatch);
        Assert.Contains("%~dp0", packageBatText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet --list-sdks", packageBatText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget install", packageBatText, StringComparison.OrdinalIgnoreCase);

        var acceptanceText = File.ReadAllText(acceptance);
        Assert.Contains("/v1/scanners", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Worker.exe", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("ParentProcessId", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("CreationDate", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("Stop-Service -Name $serviceName", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("WaitForStatus", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("Assert-ProcessIdentityGone", acceptanceText, StringComparison.Ordinal);
        Assert.Contains("Assert-NoPackageWorkers", acceptanceText, StringComparison.Ordinal);

        var upgradeText = File.ReadAllText(upgradeAcceptance);
        Assert.Contains("$candidateWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $candidateServicePid)", upgradeText, StringComparison.Ordinal);
        Assert.Contains("$candidateWorkerIdentities", upgradeText, StringComparison.Ordinal);
        Assert.Contains("Candidate NAPS2.Worker after Stop-Service", upgradeText, StringComparison.Ordinal);

        var workerCapture = upgradeText.IndexOf("$candidateWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $candidateServicePid)", StringComparison.Ordinal);
        var workerStop = upgradeText.IndexOf("Stop-Service -Name $serviceName", workerCapture, StringComparison.Ordinal);
        Assert.True(workerCapture >= 0 && workerStop > workerCapture);
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
