using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class BaselineContractTests
{
    [Fact]
    public void CurrentPairPaths_AreResolvedFromPolicyContent()
    {
        using var policy = JsonDocument.Parse("""
            {
              "contract_conformance": {
                "current": {
                  "contract": { "path": "contracts/future-contract.json" },
                  "conformance": { "path": "contracts/future-conformance.json" }
                }
              }
            }
            """);

        var paths = ResolveCurrentPairPaths(policy.RootElement);

        Assert.Equal("contracts/future-contract.json", paths.ContractPath);
        Assert.Equal("contracts/future-conformance.json", paths.ConformancePath);
    }

    [Fact]
    public void CurrentContractPair_IsAcceptedAndReferencesExistingEvidence()
    {
        var root = FindRepositoryRoot();
        var policyPath = Path.Combine(root, "repo-policy.json");

        using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
        var paths = ResolveCurrentPairPaths(policy.RootElement);

        var contractPath = Path.Combine(root, ToPlatformPath(paths.ContractPath));
        var conformancePath = Path.Combine(root, ToPlatformPath(paths.ConformancePath));

        Assert.True(File.Exists(contractPath), $"Отсутствует current contract: {paths.ContractPath}");
        Assert.True(File.Exists(conformancePath), $"Отсутствует current conformance: {paths.ConformancePath}");

        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        using var conformance = JsonDocument.Parse(File.ReadAllText(conformancePath));

        var contractConformance = policy.RootElement.GetProperty("contract_conformance");
        var acceptedState = contractConformance.GetProperty("accepted_state");
        var expectedStatus = Assert.IsType<string>(acceptedState.GetProperty("status").GetString());
        var expectedAccepted = acceptedState.GetProperty("accepted").GetBoolean();

        Assert.Equal(expectedStatus, contract.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedAccepted, contract.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(expectedStatus, conformance.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedAccepted, conformance.RootElement.GetProperty("accepted").GetBoolean());

        var contractSchema = Assert.IsType<string>(contract.RootElement.GetProperty("schema").GetString());
        Assert.Equal(contractSchema, conformance.RootElement.GetProperty("contract").GetString());
        Assert.Equal(
            NormalizeRepositoryPath(paths.ConformancePath),
            NormalizeRepositoryPath(Assert.IsType<string>(contract.RootElement.GetProperty("conformanceCorpus").GetString())));

        Assert.Equal(
            "webassist",
            contract.RootElement.GetProperty("repositoryModel").GetProperty("exportRoot").GetString());

        foreach (var pathElement in conformance.RootElement.GetProperty("requiredRepositoryPaths").EnumerateArray())
        {
            var relativePath = Assert.IsType<string>(pathElement.GetString());
            Assert.True(
                File.Exists(Path.Combine(root, ToPlatformPath(relativePath))),
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

    private static CurrentPairPaths ResolveCurrentPairPaths(JsonElement policyRoot)
    {
        var current = policyRoot
            .GetProperty("contract_conformance")
            .GetProperty("current");

        return new CurrentPairPaths(
            Assert.IsType<string>(current.GetProperty("contract").GetProperty("path").GetString()),
            Assert.IsType<string>(current.GetProperty("conformance").GetProperty("path").GetString()));
    }

    private static string NormalizeRepositoryPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string ToPlatformPath(string path) =>
        NormalizeRepositoryPath(path).Replace('/', Path.DirectorySeparatorChar);

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

    private sealed record CurrentPairPaths(string ContractPath, string ConformancePath);
}
