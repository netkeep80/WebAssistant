using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
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
    private const string WorkerPackageId = "WebAssistant.NAPS2.Sdk.Worker.Win32";
    private const string WorkerPackageVersion = "1.3.0-webassistant.1.450cba65";
    private const string WorkerPackageFile = "WebAssistant.NAPS2.Sdk.Worker.Win32.1.3.0-webassistant.1.450cba65.nupkg";
    private const string PreviousPackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg";
    private const string OlderPackageFile = "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg";
    private const string UpstreamCommit = "450cba65aaffe6387041050a573051a64cd80fe9";
    private const string PreviousPackageSha256 = "e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915";
    private const string OlderPackageSha256 = "2dbc6e96cf0d46a554318f3224561861e669dd09b60fc618319c53fed10dcc9f";
    private const string CurrentPackageSha256 = "34bf8c94b851dcabad12f6cb50abc504a14010b44b3d6db592efec6ad310e0fc";
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
    public void SourceAlignedWin32WorkerPackage_IsRepositoryOwnedAndContainsWorker()
    {
        var packagePath = GetPackagePath(WorkerPackageFile);
        Assert.True(File.Exists(packagePath), $"Не найден repository-owned Win32 worker package: {packagePath}");

        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(archive.Entries, entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = Assert.Single(nuspec.Descendants(), element => element.Name.LocalName == "metadata");
        var id = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "id");
        var version = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "version");

        Assert.Equal(WorkerPackageId, id.Value);
        Assert.Equal(WorkerPackageVersion, version.Value);
        Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, "contentFiles/NAPS2.Worker.exe", StringComparison.Ordinal));
        Assert.Single(archive.Entries, entry =>
            string.Equals(
                entry.FullName,
                "build/WebAssistant.NAPS2.Sdk.Worker.Win32.targets",
                StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry =>
            string.Equals(
                entry.FullName,
                "build/NAPS2.Sdk.Worker.Win32.targets",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SourceAlignedWin32Worker_HasMonotonicFileVersion()
    {
        using var archive = ZipFile.OpenRead(GetPackagePath(WorkerPackageFile));
        var workerEntry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, "contentFiles/NAPS2.Worker.exe", StringComparison.Ordinal));

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"webassistant-naps2-worker-{Guid.NewGuid():N}.exe");
        try
        {
            workerEntry.ExtractToFile(temporaryPath);
            var fileVersion = FileVersionInfo.GetVersionInfo(temporaryPath).FileVersion;
            Assert.Equal("8.3.0.1", fileVersion);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    [Fact]
    public void ProductReference_UsesRepositoryOwnedSourceAlignedWorker()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "webassist", "src", "WebAssistant", "WebAssistant.csproj");
        var project = XDocument.Load(projectPath);
        var packageReferences = project
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .ToList();

        var workerReference = Assert.Single(packageReferences, reference =>
            string.Equals(reference.Attribute("Include")?.Value, WorkerPackageId, StringComparison.Ordinal));

        Assert.Equal(WorkerPackageVersion, workerReference.Attribute("Version")?.Value);
        Assert.DoesNotContain(packageReferences, reference =>
            string.Equals(reference.Attribute("Include")?.Value, "NAPS2.Sdk.Worker.Win32", StringComparison.Ordinal));
    }

    [Fact]
    public void RebuildRecipe_ProducesSourceAlignedWin32WorkerFromSamePinnedTree()
    {
        var root = FindRepositoryRoot();
        var rebuildPath = Path.Combine(root, "webassist", "vendor", "naps2", "rebuild-fixed-sdk.sh");
        var rebuild = File.ReadAllText(rebuildPath);
        var provenancePath = Path.Combine(root, "webassist", "vendor", "naps2", "README.md");
        var provenance = File.ReadAllText(provenancePath);

        Assert.Contains(WorkerPackageVersion, rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk.Worker.Build/NAPS2.Sdk.Worker.Build.csproj", rebuild, StringComparison.Ordinal);
        Assert.Contains("NAPS2.Sdk.Worker.Win32/NAPS2.Sdk.Worker.Win32.csproj", rebuild, StringComparison.Ordinal);
        Assert.Contains("8.3.0.1</FileVersion>", rebuild, StringComparison.Ordinal);

        Assert.Contains(WorkerPackageId, provenance, StringComparison.Ordinal);
        Assert.Contains(WorkerPackageVersion, provenance, StringComparison.Ordinal);
        Assert.Contains(WorkerPackageFile, provenance, StringComparison.Ordinal);
        Assert.Contains(UpstreamCommit, provenance, StringComparison.Ordinal);
        Assert.Contains("source-aligned", provenance, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains($"`{CurrentPackageSha256}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`{PreviousPackageSha256}`", provenance, StringComparison.Ordinal);
        Assert.Contains($"`{OlderPackageSha256}`", provenance, StringComparison.Ordinal);
        Assert.Contains("FileVersion=8.3.0.4", provenance, StringComparison.Ordinal);
        Assert.Contains("AssemblyVersion=8.3.0.0", provenance, StringComparison.Ordinal);
        Assert.Contains("остаётся неизменяемым", provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RebuildRecipe_ContainsDeterministicWorkerLifecyclePatchLineage()
    {
        var root = FindRepositoryRoot();
        var rebuildPath = Path.Combine(root, "webassist", "vendor", "naps2", "rebuild-fixed-sdk.sh");
        var rebuild = File.ReadAllText(rebuildPath);

        Assert.Contains(PackageVersion, rebuild, StringComparison.Ordinal);
        Assert.Contains("8.3.0.4</FileVersion>", rebuild, StringComparison.Ordinal);
        Assert.Contains("8.3.0.0</AssemblyVersion>", rebuild, StringComparison.Ordinal);
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
