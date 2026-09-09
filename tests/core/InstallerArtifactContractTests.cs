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
    public void InstallerAcceptanceConsumers_WhenPresent_NeverRebuildOrRewriteAcceptedPayload()
    {
        AssertImmutableConsumer(
            "tests/windows-service/run-installer-acceptance.ps1",
            new[] { "dotnet publish", "package.bat", "package.ps1", "Set-Content", "Add-Content", "Out-File" });

        AssertImmutableConsumer(
            "tests/linux-systemd/run-installer-acceptance.sh",
            new[] { "dotnet publish", "package.bat", "package.sh", "sed -i", "cat >", "printf >" });
    }

    private static void AssertImmutableConsumer(string relativePath, IReadOnlyList<string> forbiddenTokens)
    {
        var fullPath = ToFullPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return;
        }

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
