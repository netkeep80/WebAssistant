using System.Text.Json.Nodes;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemCandidateContractTests
{
    [Fact]
    public void CandidateV03_DeclaresRootedFilesystemExchangeTransaction()
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
            "/v1/filesystem/list",
            "/v1/filesystem/file",
            "/v1/filesystem/directory",
            "/v1/filesystem/move",
            "destination_exists",
            "directory_not_empty",
            "unsafe_link",
            "hardlink_rejected",
            "blocked_file_type",
            "locked",
            "filesystem_unavailable",
            "atomic",
            "no-replace",
            "stream",
            "opaque",
            "200",
            "1000",
            "/filesystem.html"
        };

        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, contractText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CandidateV03_ConformanceContainsExecutableFilesystemVectors()
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
            "filesystem",
            "atomic",
            "no-replace",
            "RootDirectory",
            "link",
            "hard-link",
            "Playwright",
            "/filesystem.html",
            "external mutation",
            "tests/core/FileSystemCandidateContractTests.cs", "tests/core/LinuxRootedFileSystemTests.cs", "tests/core/WindowsRootedFileSystemTests.cs", "tests/core/HttpFileSystemContractTests.cs", "tests/core/FilePublicationTests.cs", "tests/core/FileSystemPageContractTests.cs", "tests/core/FileSystemBrowserTests.cs"
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
