using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsPackagingSdkResolutionTests
{
    [Fact]
    public void WindowsPackage_PinsResolvedDotNetForWholePackagingTransaction()
    {
        var batch = ReadRequired("webassist/build/windows/package.bat");
        var powershell = ReadRequired("webassist/build/windows/package.ps1");

        Assert.Contains("WEBASSISTANT_DOTNET_EXE", batch, StringComparison.Ordinal);
        Assert.Contains("%ProgramFiles%\\dotnet\\dotnet.exe", batch, StringComparison.Ordinal);

        Assert.Contains("WEBASSISTANT_DOTNET_EXE", powershell, StringComparison.Ordinal);
        Assert.Contains("$dotnetExecutable", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("& dotnet ", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("(& dotnet ", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPackage_AfterWinget_ProbesCanonicalProgramFilesDotNetDirectly()
    {
        var batch = ReadRequired("webassist/build/windows/package.bat");

        var wingetInstall = batch.IndexOf(
            "winget install --id Microsoft.DotNet.SDK.10",
            StringComparison.Ordinal);
        Assert.True(wingetInstall >= 0, "Windows package must bootstrap .NET SDK 10 through winget when needed.");

        var canonicalProbeAfterBootstrap = batch.IndexOf(
            "%ProgramFiles%\\dotnet\\dotnet.exe",
            wingetInstall,
            StringComparison.Ordinal);
        Assert.True(
            canonicalProbeAfterBootstrap > wingetInstall,
            "After winget install, package.bat must probe the canonical Program Files dotnet.exe directly instead of relying on PATH refresh.");
    }

    [Fact]
    public void WindowsPackage_ValidatesResolvedExecutableHasSdk10BeforePowerShellBuild()
    {
        var batch = ReadRequired("webassist/build/windows/package.bat");

        Assert.Contains("--list-sdks", batch, StringComparison.Ordinal);
        Assert.Contains("10.", batch, StringComparison.Ordinal);
        Assert.Contains(
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File",
            batch,
            StringComparison.Ordinal);
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
