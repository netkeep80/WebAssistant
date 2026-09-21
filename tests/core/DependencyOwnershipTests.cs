using System.Diagnostics;\nusing System.IO.Compression;\nusing System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using NAPS2.Scan;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class DependencyOwnershipTests
{
    private const string PackageId = "WebAssistant.NAPS2.Sdk";
    private const string PackageVersion = "1.3.0-webassistant.4.450cba65";
    private const string PackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.4.450cba65.nupkg";
    private const string PreviousPackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg";\n    private const string OlderPackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg";
    private const string UpstreamCommit = "450cba65aaffe6387041050a573051a64cd80fe9";
    private const string PreviousPackageSha256 = "e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915";\n    private const string OlderPackageSha256 = "2dbc6e96cf0d46a554318f3224561861e669dd09b60fc618319c53fed10dcc9f";\n    private const string CurrentPackageSha256 = "34bf8c94b851dcabad12f6cb50abc504a14010b44b3d6db592efec6ad310e0fc";
    private const long MaxPackageBytes = 1024L * 1024L;

    [Fact]
    public void FixedSdkPackage_IsPresentAndBounded()
    {
        var packagePath = GetPackagePath(PackageFile);

        Assert.True(File.Exists(packagePath), $"Не найден fixed SDK package: {packagePath}");

        var packageInfo = new FileInfo(packagePath);
        Assert.True(
            packageInfo.Length < MaxPackageBytes,
            $"Fixed SDK package вырос до {packageInfo.Length} байт при лимите {MaxPackageBytes - 1}.");
    }

    [Fact]
    public void FixedSdkPackage_AndPredecessorsMatchPinnedSha256()
    {
        foreach (var (packageFile, expectedSha256) in new[]
        {
            (PackageFile, CurrentPackageSha256),
            (PreviousPackageFile, PreviousPackageSha256),
            (OlderPackageFile, OlderPackageSha256)
        })
        {
            var packagePath = GetPackagePath(packageFile);
            Assert.True(File.Exists(packagePath), $"Не найден pinned SDK package: {packagePath}");

            using var package = File.OpenRead(packagePath);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            Assert.Equal(expectedSha256, actualSha256);
        }
    }

    [Fact]
    public void FixedSdkPackage_HasMonotonicFileVersionAndStableAssemblyIdentity()
    {
        using var archive = ZipFile.OpenRead(GetPackagePath(PackageFile));
        var dllEntry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, "lib/net10.0/NAPS2.Sdk.dll", StringComparison.Ordinal));

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"webassistant-naps2-{Guid.NewGuid():N}.dll");
        try
        {
            dllEntry.ExtractToFile(temporaryPath);

            var assemblyVersion = AssemblyName.GetAssemblyName(temporaryPath).Version;
            var fileVersion = FileVersionInfo.GetVersionInfo(temporaryPath).FileVersion;

            Assert.Equal(new Version(8, 3, 0, 0), assemblyVersion);
            Assert.Equal("8.3.0.4", fileVersion);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    [Fact]
    public void FixedSdk_ExposesNullableFeederPaperState()
    {
        var property = typeof(PaperSourceCaps).GetProperty("FeederHasPaper");

        Assert.NotNull(property);
        Assert.Equal(typeof(bool?), property.PropertyType);
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

        var fixedSdkReference = Assert.Single(packageReferences, reference =>
            string.Equals(reference.Attribute("Include")?.Value, PackageId, StringComparison.Ordinal));

        Assert.Equal(PackageVersion, fixedSdkReference.Attribute("Version")?.Value);
        Assert.DoesNotContain(packageReferences, reference =>
            string.Equals(reference.Attribute("Include")?.Value, "NAPS2.Sdk", StringComparison.Ordinal) &&
            string.Equals(reference.Attribute("Version")?.Value, "1.3.0", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedSdkPackageNuspec_MatchesPinnedPackageIdentity()
    {
        using var archive = ZipFile.OpenRead(GetPackagePath(PackageFile));
        var nuspecEntry = Assert.Single(archive.Entries, entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = Assert.Single(
            nuspec.Descendants(),
            element => element.Name.LocalName == "metadata");
        var id = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "id");
        var version = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "version");

        Assert.Equal(PackageId, id.Value);
        Assert.Equal(PackageVersion, version.Value);
    }

    [Fact]
    public void ProvenanceMetadata_PinsExactUpstreamCommitAndPackageIdentity()
    {
        var root = FindRepositoryRoot();
        var provenancePath = Path.Combine(root, "webassist", "vendor", "naps2", "README.md");
        var provenance = File.ReadAllText(provenancePath);

        Assert.Contains($"`{PackageId}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`{PackageVersion}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`../nuget/{PackageFile}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`{UpstreamCommit}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`{CurrentPackageSha256}`", provenance, StringComparison.Ordinal);\n        Assert.Contains($"`{PreviousPackageSha256}`", provenance, StringComparison.Ordinal);\n        Assert.Contains($"`{OlderPackageSha256}`", provenance, StringComparison.Ordinal);\n        Assert.Contains("FileVersion=8.3.0.4", provenance, StringComparison.Ordinal);\n        Assert.Contains("AssemblyVersion=8.3.0.0", provenance, StringComparison.Ordinal);
        Assert.Contains("остаётся неизменяемым", provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RebuildRecipe_ContainsDeterministicWorkerLifecyclePatchLineage()
    {
        var root = FindRepositoryRoot();
        var rebuildPath = Path.Combine(root, "webassist", "vendor", "naps2", "rebuild-fixed-sdk.sh");
        var rebuild = File.ReadAllText(rebuildPath);

        Assert.Contains(PackageVersion, rebuild, StringComparison.Ordinal);\n        Assert.Contains("8.3.0.4</FileVersion>", rebuild, StringComparison.Ordinal);\n        Assert.Contains("8.3.0.0</AssemblyVersion>", rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk/Remoting/Worker/WorkerContext.cs", rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk/Remoting/Worker/WorkerFactory.cs", rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk/Remoting/Worker/IWorkerFactory.cs", rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk/Scan/ScanningContext.cs", rebuild, StringComparison.Ordinal);
        Assert.Contains("_stopTask", rebuild, StringComparison.Ordinal);
        Assert.Contains("StopAllWorkersAsync", rebuild, StringComparison.Ordinal);
        Assert.Contains("ShutdownAsync", rebuild, StringComparison.Ordinal);
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

    private static string GetPackagePath(string packageFile)
    {
        var root = FindRepositoryRoot();
        return Path.Combine(root, "webassist", "vendor", "nuget", packageFile);
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

#pragma warning restore CA2252
