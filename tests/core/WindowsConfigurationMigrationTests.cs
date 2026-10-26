using WebAssistant.UpgradePreflight;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsConfigurationMigrationTests : IDisposable
{
    private readonly string root;
    private readonly string legacyRoot;
    private readonly string programDataRoot;

    public WindowsConfigurationMigrationTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-preflight-config-tests",
            Guid.NewGuid().ToString("N"));
        legacyRoot = Path.Combine(root, "legacy");
        programDataRoot = Path.Combine(root, "program-data");
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(programDataRoot);
    }

    [Fact]
    public void Migrate_CopiesLegacyConfigByteForByteBeforeInstallerReplacement()
    {
        var source = Path.Combine(legacyRoot, "appsettings.json");
        var bytes = new byte[]
        {
            0x7B, 0x0D, 0x0A, 0x20, 0x20, 0x2F, 0x2F, 0x20,
            0x6F, 0x6C, 0x64, 0x0D, 0x0A, 0x7D
        };
        File.WriteAllBytes(source, bytes);

        WindowsConfigurationMigration.Migrate(
            legacyRoot,
            programDataRoot);

        Assert.Equal(
            bytes,
            File.ReadAllBytes(
                Path.Combine(
                    programDataRoot,
                    "WebAssistant",
                    "config",
                    "appsettings.json")));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void Migrate_DoesNotOverwriteExistingPersistentConfig()
    {
        var source = Path.Combine(legacyRoot, "appsettings.json");
        File.WriteAllText(source, "legacy");

        var destinationRoot = Path.Combine(
            programDataRoot,
            "WebAssistant",
            "config");
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Combine(
            destinationRoot,
            "appsettings.json");
        File.WriteAllText(destination, "persistent");

        WindowsConfigurationMigration.Migrate(
            legacyRoot,
            programDataRoot);

        Assert.Equal("persistent", File.ReadAllText(destination));
        Assert.Equal(
            "legacy",
            File.ReadAllText(
                Path.Combine(
                    destinationRoot,
                    "legacy-appsettings.json")));
    }

    [Fact]
    public void Migrate_WritesMarkerAndDoesNotRepeatLegacyConflictOnRepair()
    {
        var source = Path.Combine(legacyRoot, "appsettings.json");
        File.WriteAllText(source, "legacy-before-upgrade");

        WindowsConfigurationMigration.Migrate(
            legacyRoot,
            programDataRoot);

        var destinationRoot = Path.Combine(
            programDataRoot,
            "WebAssistant",
            "config");
        var destination = Path.Combine(
            destinationRoot,
            "appsettings.json");
        var markerPath = Path.Combine(
            destinationRoot,
            WindowsConfigurationMigration.LifecycleMarkerFileName);

        Assert.True(File.Exists(markerPath));
        Assert.Equal(
            "legacy-before-upgrade",
            File.ReadAllText(destination));

        File.WriteAllText(source, "candidate-package-seed");
        WindowsConfigurationMigration.Migrate(
            legacyRoot,
            programDataRoot);

        Assert.Equal(
            "legacy-before-upgrade",
            File.ReadAllText(destination));
        Assert.False(
            File.Exists(
                Path.Combine(
                    destinationRoot,
                    "legacy-appsettings.json")));
    }

    [Fact]
    public void Migrate_DoesNotTreatExampleAsRuntimeConfiguration()
    {
        File.WriteAllText(
            Path.Combine(
                legacyRoot,
                WindowsConfigurationMigration.ExampleFileName),
            "example");

        WindowsConfigurationMigration.Migrate(
            legacyRoot,
            programDataRoot);

        Assert.False(
            File.Exists(
                Path.Combine(
                    programDataRoot,
                    "WebAssistant",
                    "config",
                    WindowsConfigurationMigration.ExampleFileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
