using System.Reflection;
using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductVersionTests
{
    private const string CanonicalSemVerPattern =
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$";

    [Fact]
    public void PersistedVersion_IsSingleCanonicalSemVerValueInsideProductRoot()
    {
        var path = ProductPath("VERSION");
        Assert.True(File.Exists(path), "Canonical product version file is missing: webassist/VERSION");

        var raw = File.ReadAllText(path).Replace("\r\n", "\n");
        var value = raw.TrimEnd('\n');

        Assert.DoesNotContain('\n', value);
        Assert.Equal(value.Trim(), value);
        Assert.Matches(CanonicalSemVerPattern, value);
    }

    [Fact]
    public void ProductionAssembly_InformationalVersionEqualsPersistedVersion()
    {
        var expected = ReadPersistedVersion();
        var actual = typeof(AgentRuntimeInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RuntimeDiagnosticsVersion_EqualsPersistedVersion()
    {
        var runtimeInfo = new AgentRuntimeInfo();

        Assert.Equal(ReadPersistedVersion(), runtimeInfo.Version);
    }

    [Fact]
    public void CrossPlatformPackaging_ConsumesAndShipsPersistedVersion()
    {
        var linux = ReadRepositoryFile("webassist/build/linux/package.sh");
        var windows = ReadRepositoryFile("webassist/build/windows/package.ps1");

        Assert.Contains("VERSION", linux);
        Assert.Contains("-p:ProductVersion=", linux);
        Assert.Contains("VERSION", windows);
        Assert.Contains("-p:ProductVersion=", windows);
    }

    private static string ReadPersistedVersion()
    {
        var path = ProductPath("VERSION");
        Assert.True(File.Exists(path), "Canonical product version file is missing: webassist/VERSION");
        return File.ReadAllText(path).Trim();
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string ProductPath(string relativePath)
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "webassist",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
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
