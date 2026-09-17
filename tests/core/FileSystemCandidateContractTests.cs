using System.Text.Json.Nodes;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemCandidateContractTests
{
    [Fact]
    public void CandidateV03_DeclaresMultiRootFilesystemExchangeTransaction()
    {
        var root = FindRepositoryRoot();
        var contractText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-contract-v0.3.json"));
        var contract = JsonNode.Parse(contractText)!.AsObject();

        Assert.Equal("candidate", contract["status"]?.GetValue<string>());
        Assert.False(contract["accepted"]!.GetValue<bool>());

        var requiredFragments = new[]
        {
            "/v1/filesystem/roots",
            "/v1/filesystem/list",
            "/v1/filesystem/file",
            "/v1/filesystem/directory",
            "/v1/filesystem/move",
            "logicalRootName",
            "[A-Za-z0-9][A-Za-z0-9._-]*",
            "filesystem_not_configured",
            "filesystem_configuration_invalid",
            "filesystem_root_not_found",
            "filesystem_root_unavailable",
            "filesystem_path_invalid",
            "destination_exists",
            "directory_not_empty",
            "unsafe_link",
            "hardlink_rejected",
            "blocked_file_type",
            "locked",
            "cross-root",
            "atomic",
            "no-replace",
            "stream",
            "opaque",
            "200",
            "1000",
            "/filesystem.html",
            "LEFT",
            "RIGHT"
        };

        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, contractText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CandidateV03_ReplacesSingleRootAuthorityWithNamedRootRegistry()
    {
        var root = FindRepositoryRoot();
        var contractText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-contract-v0.3.json"));

        Assert.Contains(
            "FILESYSTEM AUTHORITY = union(configured named roots)",
            contractText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "physicalRootPath",
            contractText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "cross-root mutation",
            contractText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Filesystem capability имеет configured RootDirectory",
            contractText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateV03_DoesNotRetainSingleRootPublicVocabulary()
    {
        var root = FindRepositoryRoot();
        var contractText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-contract-v0.3.json"));

        Assert.DoesNotContain("RootDirectory", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem_unavailable", contractText, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateV03_ConformanceContainsExecutableMultiRootVectors()
    {
        var root = FindRepositoryRoot();
        var conformanceText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-conformance-v0.3.json"));
        var conformance = JsonNode.Parse(conformanceText)!.AsObject();

        Assert.Equal("candidate", conformance["status"]?.GetValue<string>());
        Assert.False(conformance["accepted"]!.GetValue<bool>());

        var requiredFragments = new[]
        {
            "multi-root",
            "/v1/filesystem/roots",
            "case-only",
            "unknown logical root",
            "filesystem_root_unavailable",
            "cross-root mutation",
            "two-panel",
            "LEFT",
            "RIGHT",
            "/filesystem.html",
            "tests/core/FileSystemRootRegistryTests.cs",
            "tests/core/MultiRootFileSystemTests.cs",
            "tests/core/HttpFileSystemContractTests.cs",
            "tests/core/FileSystemPageContractTests.cs",
            "tests/core/FileSystemBrowserTests.cs"
        };

        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, conformanceText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AcceptedV02_RemainsAcceptedAndDoesNotContainFilesystemRoutes()
    {
        var root = FindRepositoryRoot();
        var contractText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-contract-v0.2.json"));
        var conformanceText = File.ReadAllText(Path.Combine(
            root,
            "contracts",
            "webassistant-conformance-v0.2.json"));
        var contract = JsonNode.Parse(contractText)!.AsObject();
        var conformance = JsonNode.Parse(conformanceText)!.AsObject();

        Assert.Equal("accepted", contract["status"]?.GetValue<string>());
        Assert.True(contract["accepted"]!.GetValue<bool>());
        Assert.Equal("accepted", conformance["status"]?.GetValue<string>());
        Assert.True(conformance["accepted"]!.GetValue<bool>());

        Assert.DoesNotContain("/v1/filesystem/list", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/filesystem/file", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/filesystem/list", conformanceText, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/filesystem/file", conformanceText, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts")) &&
                Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}
