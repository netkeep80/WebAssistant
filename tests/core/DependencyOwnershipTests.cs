using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DependencyOwnershipTests
{
    private const string PackageId = "WebAssistant.NAPS2.Sdk";
    private const string PackageVersion = "1.3.0-webassistant.1.450cba65";
    private const string PackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.1.450cba65.nupkg";
    private const string UpstreamCommit = "450cba65aaffe6387041050a573051a64cd80fe9";
    private const string ExpectedPackageSha256 = "capture-from-first-ci";
    private const long MaxPackageBytes = 1024L * 1024L;

    [Fact]
    public void FixedSdkPackage_IsPresentBoundedAndMatchesPinnedSha256()
    {
        var packagePath = GetPackagePath();

        Assert.True(File.Exists(packagePath), $"Не найден fixed SDK package: {packagePath}");

        var packageInfo = new FileInfo(packagePath);
        Assert.True(
            packageInfo.Length < MaxPackageBytes,
            $"Fixed SDK package вырос до {packageInfo.Length} байт при лимите {MaxPackageBytes - 1}.");

        using var package = File.OpenRead(packagePath);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();

        Assert.Equal(ExpectedPackageSha256, actualSha256);
    }

    [Fact]
    public void ProductReference_MatchesPinnedPackageIdentityAndRejectsOfficialSdk()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "webassist", "src", "WebAssistant", "WebAssistant.csproj");
        var project = XDocument.Load(projectPath);
        var packageReferences = project
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .ToList();

        var fixedSdkReference = Assert.Single(packageReferences.Where(reference =>
            string.Equals(reference.Attribute("Include")?.Value, PackageId, StringComparison.Ordinal)));

        Assert.Equal(PackageVersion, fixedSdkReference.Attribute("Version")?.Value);
        Assert.DoesNotContain(packageReferences, reference =>
            string.Equals(reference.Attribute("Include")?.Value, "NAPS2.Sdk", StringComparison.Ordinal) &&
            string.Equals(reference.Attribute("Version")?.Value, "1.3.0", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedSdkPackageNuspec_MatchesPinnedPackageIdentity()
    {
        using var archive = ZipFile.OpenRead(GetPackagePath());
        var nuspecEntry = Assert.Single(archive.Entries.Where(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)));

        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = Assert.Single(nuspec.Descendants().Where(element => element.Name.LocalName == "metadata"));
        var id = Assert.Single(metadata.Elements().Where(element => element.Name.LocalName == "id"));
        var version = Assert.Single(metadata.Elements().Where(element => element.Name.LocalName == "version"));

        Assert.Equal(PackageId, id.Value);
        Assert.Equal(PackageVersion, version.Value);
    }

    [Fact]
    public void ProvenanceMetadata_PinsExactUpstreamCommitAndPackageIdentity()
    {
        var root = FindRepositoryRoot();
        var provenancePath = Path.Combine(root, "webassist", "vendor", "naps2", "README.md");
        var provenance = File.ReadAllText(provenancePath);

        Assert.Contains($"- ID: `{PackageId}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"- version: `{PackageVersion}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"- file: `../nuget/{PackageFile}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"- exact source commit: `{UpstreamCommit}`", provenance, StringComparison.Ordinal);
    }

    [Fact]
    public void NuGetConfig_ExposesRepositoryOwnedFixedSdkSource()
    {
        var root = FindRepositoryRoot();
        var configPath = Path.Combine(root, "webassist", "NuGet.Config");
        var config = XDocument.Load(configPath);
        var sources = config
            .Descendants()
            .Where(element => element.Name.LocalName == "add")
            .ToList();

        Assert.Contains(sources, source =>
            string.Equals(source.Attribute("key")?.Value, "webassistant-fixed-sdk", StringComparison.Ordinal) &&
            string.Equals(source.Attribute("value")?.Value, "vendor/nuget", StringComparison.Ordinal));
    }

    private static string GetPackagePath()
    {
        var root = FindRepositoryRoot();
        return Path.Combine(root, "webassist", "vendor", "nuget", PackageFile);
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

        throw new InvalidOperationException("Не найден корень репозитория.");
    }
}
