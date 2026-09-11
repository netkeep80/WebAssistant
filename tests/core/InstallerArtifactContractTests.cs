using System.Text.RegularExpressions;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class InstallerArtifactContractTests
{
    [Fact]
    public void Producers_ConstructExactVersionedCanonicalArtifactNames()
    {
        var version = ReadRequired("webassist/VERSION").Trim();
        Assert.Matches(new Regex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$", RegexOptions.CultureInvariant), version);

        var windows = ReadRequired("webassist/build/windows/package.ps1");
        var linux = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("$artifactName = \"WebAssistant-win-x64-$version.exe\"", windows, StringComparison.Ordinal);
        Assert.Contains("artifact_name=\"WebAssistant-linux-x64-${version}.zip\"", linux, StringComparison.Ordinal);

        Assert.DoesNotContain("WebAssistant-win-x64.exe", windows, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebAssistant-linux-x64.zip", linux, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Producers_EmitChecksumAndProvenanceNextToCanonicalArtifact()
    {
        var windows = ReadRequired("webassist/build/windows/package.ps1");
        var linux = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("$artifactPath", windows, StringComparison.Ordinal);
        Assert.Contains("$artifactPath.sha256", windows, StringComparison.Ordinal);
        Assert.Contains("$artifactPath.provenance.json", windows, StringComparison.Ordinal);
        Assert.Contains("write-provenance.ps1", windows, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("$artifact_path", linux, StringComparison.Ordinal);
        Assert.Contains("${artifact_path}.sha256", linux, StringComparison.Ordinal);
        Assert.Contains("${artifact_path}.provenance.json", linux, StringComparison.Ordinal);
        Assert.Contains("write-provenance.sh", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceWriters_DefineRequiredIdentityFieldsAndClosedConfigModeEnum()
    {
        var powershell = ReadRequired("webassist/build/common/write-provenance.ps1");
        var shell = ReadRequired("webassist/build/common/write-provenance.sh");

        var requiredFields = new[]
        {
            "artifact",
            "version",
            "sourceSha",
            "rid",
            "sha256",
            "size",
            "sdkVersion",
            "configMode",
            "packageEntrypoint"
        };

        foreach (var field in requiredFields)
        {
            Assert.Contains(field, powershell, StringComparison.Ordinal);
            Assert.Contains(field, shell, StringComparison.Ordinal);
        }

        Assert.Contains("source-appsettings", powershell, StringComparison.Ordinal);
        Assert.Contains("generated-default", powershell, StringComparison.Ordinal);
        Assert.Contains("source-appsettings", shell, StringComparison.Ordinal);
        Assert.Contains("generated-default", shell, StringComparison.Ordinal);

        Assert.Contains(
            "ValidateSet(\"source-appsettings\", \"generated-default\")",
            powershell,
            StringComparison.Ordinal);
        Assert.Contains(
            "source-appsettings|generated-default",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxProducer_StagesArchiveBasenameAsSingleRootBeforeCompression()
    {
        var linux = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("package_root_name=\"${artifact_name%.zip}\"", linux, StringComparison.Ordinal);
        Assert.Contains("package_root=\"$staging_root/$package_root_name\"", linux, StringComparison.Ordinal);
        Assert.Contains("cd -- \"$staging_root\"", linux, StringComparison.Ordinal);
        Assert.Contains("zip -q -r \"$artifact_path\" \"$package_root_name\"", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxInstallerAcceptance_ConsumesCanonicalSingleRootWithoutRebuild()
    {
        var consumer = ReadRequired("tests/linux-systemd/run-installer-acceptance.sh");

        Assert.Contains("payload_root=\"$extract_root/${artifact_name%.zip}\"", consumer, StringComparison.Ordinal);
        Assert.Contains("test -x \"$payload_root/app/WebAssistant\"", consumer, StringComparison.Ordinal);
        Assert.Contains("inside_version=\"$(tr -d '\\r\\n' < \"$payload_root/VERSION\")\"", consumer, StringComparison.Ordinal);
        Assert.Contains("run-systemd-acceptance.sh\" \"$payload_root\"", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstaller_UsesPinnedWix7MachineWideServiceBundle()
    {
        var props = ReadRequired("webassist/build/windows/installer/Directory.Build.props");
        var packageProject = ReadRequired("webassist/build/windows/installer/WebAssistant.Package.wixproj");
        var packageSource = ReadRequired("webassist/build/windows/installer/Package.wxs");
        var bundleProject = ReadRequired("webassist/build/windows/installer/WebAssistant.Bundle.wixproj");
        var bundleSource = ReadRequired("webassist/build/windows/installer/Bundle.wxs");

        Assert.Contains("WixToolset.Sdk/7.0.0", packageProject, StringComparison.Ordinal);
        Assert.Contains("WixToolset.Sdk/7.0.0", bundleProject, StringComparison.Ordinal);
        Assert.Contains("<AcceptEula>wix7</AcceptEula>", props, StringComparison.Ordinal);
        Assert.Contains("<InstallerPlatform>x64</InstallerPlatform>", props, StringComparison.Ordinal);
        Assert.Contains("WixToolset.BootstrapperApplications.wixext", bundleProject, StringComparison.Ordinal);
        Assert.Contains("Version=\"7.0.0\"", bundleProject, StringComparison.Ordinal);

        Assert.Contains("Scope=\"perMachine\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("ProgramFiles64Folder", packageSource, StringComparison.Ordinal);
        Assert.Contains("ServiceInstall", packageSource, StringComparison.Ordinal);
        Assert.Contains("Name=\"WebAssistant\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("Start=\"auto\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("Type=\"ownProcess\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("ServiceControl", packageSource, StringComparison.Ordinal);
        Assert.Contains("Stop=\"both\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("Remove=\"uninstall\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("$(var.PayloadRoot)", packageSource, StringComparison.Ordinal);
        Assert.Contains("$(var.ProductVersion)", packageSource, StringComparison.Ordinal);

        Assert.Contains("<Bundle", bundleSource, StringComparison.Ordinal);
        Assert.Contains("Version=\"$(var.ProductVersion)\"", bundleSource, StringComparison.Ordinal);
        Assert.Contains("WixStandardBootstrapperApplication", bundleSource, StringComparison.Ordinal);
        Assert.Contains("MsiPackage", bundleSource, StringComparison.Ordinal);
        Assert.Contains("Visible=\"no\"", bundleSource, StringComparison.Ordinal);
        Assert.Contains("ForcePerMachine=\"yes\"", bundleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUpgradeAcceptance_PinsImmutableHistoricalInstallerEvidence()
    {
        var workflow = ReadRequired(".github/workflows/windows-service.yml");
        var harness = ReadRequired("tests/windows-service/run-upgrade-acceptance.ps1");

        Assert.Contains("34485513571", workflow, StringComparison.Ordinal);
        Assert.Contains("10155509111", workflow, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-win-x64-0.3.21.exe", harness, StringComparison.Ordinal);
        Assert.Contains("77a5c66c431c746d2be2f283640c7951730911eb", harness, StringComparison.Ordinal);
        Assert.Contains("68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271", harness, StringComparison.Ordinal);
        Assert.Contains("/v1/scanners", harness, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Worker.exe", harness, StringComparison.Ordinal);

        Assert.DoesNotContain("dotnet publish", harness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package.bat", harness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package.ps1", harness, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerAcceptanceConsumers_NeverRebuildOrRewriteAcceptedPayload()
    {
        AssertImmutableConsumer(
            "tests/windows-service/run-installer-acceptance.ps1",
            new[] { "dotnet publish", "package.bat", "package.ps1", "Set-Content", "Add-Content", "Out-File" });

        AssertImmutableConsumer(
            "tests/windows-service/run-upgrade-acceptance.ps1",
            new[] { "dotnet publish", "package.bat", "package.ps1" });

        AssertImmutableConsumer(
            "tests/linux-systemd/run-installer-acceptance.sh",
            new[] { "dotnet publish", "package.bat", "package.sh", "sed -i", "cat >", "printf >" });
    }

    private static void AssertImmutableConsumer(string relativePath, IReadOnlyList<string> forbiddenTokens)
    {
        var fullPath = ToFullPath(relativePath);
        Assert.True(File.Exists(fullPath), $"Required installer acceptance consumer is missing: {relativePath}");

        var text = File.ReadAllText(fullPath);
        foreach (var token in forbiddenTokens)
        {
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("sha256", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provenance", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRequired(string relativePath)
    {
        var path = ToFullPath(relativePath);
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string ToFullPath(string relativePath) =>
        Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
