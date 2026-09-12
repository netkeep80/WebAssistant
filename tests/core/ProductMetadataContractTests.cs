using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductMetadataContractTests
{
    private const string OverridePath = "webassist/src/WebAssistant/product-metadata.json";

    [Fact]
    public void GithubOnlyIgnoreBoundary_AndCanonicalMetadataInputs_AreDefined()
    {
        var rootIgnore = ReadRequired(".gitignore");
        var exportedIgnore = ReadRequired("webassist/.gitignore");

        Assert.Contains(OverridePath, rootIgnore, StringComparison.Ordinal);
        Assert.DoesNotContain("src/WebAssistant/product-metadata.json", exportedIgnore, StringComparison.Ordinal);

        var defaults = ReadRequired("webassist/build/common/product-metadata.defaults.json");
        Assert.Contains("webassistant-product-metadata/v1", defaults, StringComparison.Ordinal);
        Assert.Contains("\"applicationName\": \"WebAssistant\"", defaults, StringComparison.Ordinal);
        Assert.Contains("\"installerBaseName\": \"WebAssistant\"", defaults, StringComparison.Ordinal);

        Assert.True(File.Exists(ToFullPath("webassist/build/common/ProductMetadataResolver/ProductMetadataResolver.csproj")));
        Assert.True(File.Exists(ToFullPath("webassist/build/common/ProductMetadataResolver/Program.cs")));
        Assert.False(File.Exists(ToFullPath(OverridePath)), "Public GitHub repository must not commit product-metadata.json.");
    }

    [Fact]
    public void CandidateV03_DefinesParameterizedProductIdentity_WithoutChangingVersionAuthority()
    {
        var contract = ReadRequired("contracts/webassistant-contract-v0.3.json");
        var conformance = ReadRequired("contracts/webassistant-conformance-v0.3.json");

        Assert.Contains("effective installer basename", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("product-metadata.json", contract, StringComparison.Ordinal);
        Assert.Contains("webassist/VERSION", contract, StringComparison.Ordinal);
        Assert.Contains("metadataMode", conformance, StringComparison.Ordinal);
        Assert.Contains("ProductMetadataContractTests.cs", conformance, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Canonical installer artifacts имеют имена WebAssistant-win-x64-<VERSION>.exe и WebAssistant-linux-x64-<VERSION>.zip",
            contract,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalProducers_UseOneResolver_AndEffectiveInstallerBasename()
    {
        var windows = ReadRequired("webassist/build/windows/package.ps1");
        var linux = ReadRequired("webassist/build/linux/package.sh");

        foreach (var producer in new[] { windows, linux })
        {
            Assert.Contains("ProductMetadataResolver", producer, StringComparison.Ordinal);
            Assert.Contains("installerBaseName", producer, StringComparison.Ordinal);
            Assert.Contains("metadataMode", producer, StringComparison.Ordinal);
            Assert.Contains("effectiveMetadataSha256", producer, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("$artifactName = \"WebAssistant-win-x64-$version.exe\"", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact_name=\"WebAssistant-linux-x64-${version}.zip\"", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveMetadata_FlowsToBinaryWixLinuxAndProvenance_WhileTechnicalIdsStayStable()
    {
        var project = ReadRequired("webassist/src/WebAssistant/WebAssistant.csproj");
        var package = ReadRequired("webassist/build/windows/installer/Package.wxs");
        var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
        var windowsProvenance = ReadRequired("webassist/build/common/write-provenance.ps1");
        var linuxProvenance = ReadRequired("webassist/build/common/write-provenance.sh");
        var linuxProducer = ReadRequired("webassist/build/linux/package.sh");

        Assert.Contains("ProductApplicationName", project, StringComparison.Ordinal);
        Assert.Contains("ProductFileDescription", project, StringComparison.Ordinal);
        Assert.Contains("ProductCompanyName", project, StringComparison.Ordinal);
        Assert.Contains("ProductCopyright", project, StringComparison.Ordinal);

        Assert.Contains("$(var.ProductDisplayName)", package, StringComparison.Ordinal);
        Assert.Contains("$(var.ProductCompanyName)", package, StringComparison.Ordinal);
        Assert.Contains("Name=\"WebAssistant\"", package, StringComparison.Ordinal);
        Assert.Contains("$(var.ProductDisplayName)", bundle, StringComparison.Ordinal);

        Assert.Contains("Description=", linuxProducer, StringComparison.Ordinal);
        foreach (var provenance in new[] { windowsProvenance, linuxProvenance })
        {
            Assert.Contains("metadataMode", provenance, StringComparison.Ordinal);
            Assert.Contains("applicationName", provenance, StringComparison.Ordinal);
            Assert.Contains("installerBaseName", provenance, StringComparison.Ordinal);
            Assert.Contains("metadataInputSha256", provenance, StringComparison.Ordinal);
            Assert.Contains("effectiveMetadataSha256", provenance, StringComparison.Ordinal);
        }
    }

    private static string ReadRequired(string relativePath)
    {
        var path = ToFullPath(relativePath);
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string ToFullPath(string relativePath) =>
        Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

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
