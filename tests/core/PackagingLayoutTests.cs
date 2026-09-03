using Xunit;

namespace WebAssistant.CoreTests;

public sealed class PackagingLayoutTests
{
    [Fact]
    public void PackagingLayout_ContainsCrossPlatformBuildAndInstallEntrypoints()
    {
        var expectedFiles = new[]
        {
            "webassist/build/linux/package.sh",
            "webassist/build/windows/package.bat",
            "webassist/build/windows/package.ps1",
            "webassist/install/linux/install.sh",
            "webassist/install/linux/uninstall.sh",
            "webassist/install/linux/webassist.service",
            "webassist/install/windows/install.bat",
            "webassist/install/windows/install.ps1",
            "webassist/install/windows/uninstall.bat",
            "webassist/install/windows/uninstall.ps1"
        };

        foreach (var relativePath in expectedFiles)
        {
            var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Packaging entrypoint is missing: {relativePath}");
        }
    }

    [Fact]
    public void LinuxPackage_IsCwdIndependentAndBuildsDirectSdkProduct()
    {
        var package = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("BASH_SOURCE[0]", package);
        Assert.Contains("src/WebAssistant/WebAssistant.csproj", package);
        Assert.Contains("dotnet-sdk-10.0", package);
        Assert.Contains("--runtime linux-x64", package);
        Assert.Contains("--self-contained true", package);
        Assert.DoesNotContain("--naps2-rpm", package);
        Assert.DoesNotContain(".rpm", package);
    }

    [Fact]
    public void LinuxInstall_UsesWebAssistServiceAndOwnedRuntimeDirectories()
    {
        var install = ReadRequired("webassist/install/linux/install.sh");
        var service = ReadRequired("webassist/install/linux/webassist.service");

        Assert.Contains("/opt/webassist", install);
        Assert.Contains("/var/log/webassist", install);
        Assert.Contains("/var/lib/webassist", install);
        Assert.Contains("webassist.service", install);
        Assert.Contains("appsettings.json", install);

        Assert.Contains("User=webassist", service);
        Assert.Contains("Group=webassist", service);
        Assert.Contains("WorkingDirectory=/opt/webassist", service);
        Assert.Contains("ExecStart=/opt/webassist/WebAssistant", service);
    }

    [Fact]
    public void WindowsPackage_BootstrapsDotNet10AndResolvesProductRootFromScript()
    {
        var batch = ReadRequired("webassist/build/windows/package.bat");
        var powershell = ReadRequired("webassist/build/windows/package.ps1");

        Assert.Contains("%~dp0", batch);
        Assert.Contains("Microsoft.DotNet.SDK.10", batch);
        Assert.Contains("$PSScriptRoot", powershell);
        Assert.Contains("src/WebAssistant/WebAssistant.csproj", powershell);
        Assert.Contains("WebAssistant.exe", powershell);
    }

    [Fact]
    public void WindowsInstall_WritesJsonRuntimeConfiguration()
    {
        var install = ReadRequired("webassist/install/windows/install.ps1");

        Assert.Contains("$serviceName = \"WebAssistant\"", install);
        Assert.Contains("ProgramFiles\\WebAssistant", install);
        Assert.Contains("ProgramData", install);
        Assert.Contains("WebAssistant = @{", install);
        Assert.Contains("Cors = @{", install);
        Assert.Contains("Enabled =", install);
        Assert.Contains("AllowedOrigins", install);
        Assert.Contains("FileSystem = @{", install);
        Assert.Contains("RootDirectory", install);
        Assert.Contains("appsettings.json", install);
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
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "webassist", "WebAssistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("WebAssistant repository root was not found.");
    }
}
