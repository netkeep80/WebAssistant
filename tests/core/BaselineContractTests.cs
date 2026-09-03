using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class BaselineContractTests
{
    [Fact]
    public void CurrentContractPair_IsAcceptedAndReferencesExistingEvidence()
    {
        var root = FindRepositoryRoot();
        var contractPath = Path.Combine(root, "contracts", "webassistant-contract-v0.1.json");
        var conformancePath = Path.Combine(root, "contracts", "webassistant-conformance-v0.1.json");

        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        using var conformance = JsonDocument.Parse(File.ReadAllText(conformancePath));

        Assert.Equal("webassistant-contract/v0.1", contract.RootElement.GetProperty("schema").GetString());
        Assert.True(contract.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "webassist",
            contract.RootElement.GetProperty("repositoryModel").GetProperty("exportRoot").GetString());

        Assert.Equal("webassistant-conformance/v0.1", conformance.RootElement.GetProperty("schema").GetString());
        Assert.True(conformance.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("webassistant-contract/v0.1", conformance.RootElement.GetProperty("contract").GetString());

        foreach (var pathElement in conformance.RootElement.GetProperty("requiredRepositoryPaths").EnumerateArray())
        {
            var relativePath = Assert.IsType<string>(pathElement.GetString());
            Assert.True(
                File.Exists(Path.Combine(root, relativePath)),
                $"Отсутствует обязательный evidence path: {relativePath}");
        }
    }

    [Fact]
    public void AutonomousProductRoot_ContainsItsOwnUsageAndApiDocumentation()
    {
        var root = FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "webassist", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "webassist", "docs", "api.md")));
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
