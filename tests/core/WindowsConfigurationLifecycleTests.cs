using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsConfigurationLifecycleTests : IDisposable
{
    private readonly string root;
    private readonly string applicationRoot;
    private readonly string programDataRoot;

    public WindowsConfigurationLifecycleTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-windows-config-tests",
            Guid.NewGuid().ToString("N"));
        applicationRoot = Path.Combine(root, "application");
        programDataRoot = Path.Combine(root, "program-data");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(programDataRoot);
    }

    [Fact]
    public void Prepare_MigratesLegacyActiveConfigurationByteForByte()
    {
        var legacy = Path.Combine(
            applicationRoot,
            WindowsConfigurationLifecycle.ActiveFileName);
        var bytes = new byte[]
        {
            0x7B, 0x0D, 0x0A, 0x20, 0x20, 0x2F, 0x2F, 0x20,
            0x6C, 0x65, 0x67, 0x61, 0x63, 0x79, 0x0D, 0x0A, 0x7D
        };
        File.WriteAllBytes(legacy, bytes);

        var state = WindowsConfigurationLifecycle.Prepare(
            applicationRoot,
            programDataRoot);

        Assert.Equal(
            Path.Combine(
                programDataRoot,
                "WebAssistant",
                "config"),
            state.ConfigurationRoot);
        Assert.Equal(bytes, File.ReadAllBytes(state.ActiveConfigurationPath));
        Assert.Equal(bytes, File.ReadAllBytes(legacy));
    }

    [Fact]
    public void Prepare_NeverOverwritesPersistentConfiguration()
    {
        var configurationRoot = Path.Combine(
            programDataRoot,
            "WebAssistant",
            "config");
        Directory.CreateDirectory(configurationRoot);
        var active = Path.Combine(
            configurationRoot,
            WindowsConfigurationLifecycle.ActiveFileName);
        var legacy = Path.Combine(
            applicationRoot,
            WindowsConfigurationLifecycle.ActiveFileName);

        File.WriteAllText(active, "persistent");
        File.WriteAllText(legacy, "legacy");

        WindowsConfigurationLifecycle.Prepare(
            applicationRoot,
            programDataRoot);

        Assert.Equal("persistent", File.ReadAllText(active));
        Assert.Equal(
            "legacy",
            File.ReadAllText(
                Path.Combine(
                    configurationRoot,
                    "legacy-appsettings.json")));
    }

    [Fact]
    public void Prepare_MigratesEnvironmentConfigurationWithoutOverwrite()
    {
        var legacy = Path.Combine(
            applicationRoot,
            "appsettings.Production.json");
        File.WriteAllText(legacy, "legacy-production");

        var configurationRoot = Path.Combine(
            programDataRoot,
            "WebAssistant",
            "config");
        Directory.CreateDirectory(configurationRoot);
        var existing = Path.Combine(
            configurationRoot,
            "appsettings.Staging.json");
        File.WriteAllText(existing, "persistent-staging");

        WindowsConfigurationLifecycle.Prepare(
            applicationRoot,
            programDataRoot);

        Assert.Equal(
            "legacy-production",
            File.ReadAllText(
                Path.Combine(
                    configurationRoot,
                    "appsettings.Production.json")));
        Assert.Equal(
            "persistent-staging",
            File.ReadAllText(existing));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
