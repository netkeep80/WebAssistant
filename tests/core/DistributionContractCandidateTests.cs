using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DistributionContractCandidateTests
{
    private const string BaselineContractPath = "contracts/webassistant-contract-v0.2.json";
    private const string BaselineConformancePath = "contracts/webassistant-conformance-v0.2.json";
    private const string CandidateContractPath = "contracts/webassistant-contract-v0.3.json";
    private const string CandidateConformancePath = "contracts/webassistant-conformance-v0.3.json";

    private static readonly string[] DistributionRequirementIds =
    [
        "WA-DIST-001",
        "WA-DIST-002",
        "WA-DIST-003",
        "WA-DIST-004",
        "WA-WIN-INSTALL-001",
        "WA-ALT-INSTALL-001",
        "WA-ARTIFACT-001",
        "WA-RELEASE-001",
        "WA-INSTALL-DOC-001"
    ];

    private static readonly string[] DistributionVectorIds =
    [
        "WA-C-DIST-PRODUCERS-001",
        "WA-C-DIST-VERSION-IDENTITY-001",
        "WA-C-PACKAGE-CONFIG-001",
        "WA-C-SELF-CONTAINED-TARGET-001",
        "WA-C-WINDOWS-INSTALLER-001",
        "WA-C-ALT-INSTALLER-001",
        "WA-C-IMMUTABLE-ARTIFACT-001",
        "WA-C-RELEASE-SAME-BYTES-001",
        "WA-C-INSTALL-GUIDE-001"
    ];

    [Fact]
    public void DistributionCandidate_PreservesV02AndAddsOnlyCandidateDistributionAuthority()
    {
        var root = FindRepositoryRoot();

        var candidateContractFullPath = ToFullPath(root, CandidateContractPath);
        var candidateConformanceFullPath = ToFullPath(root, CandidateConformancePath);
        Assert.True(File.Exists(candidateContractFullPath), $"Отсутствует candidate contract: {CandidateContractPath}");
        Assert.True(File.Exists(candidateConformanceFullPath), $"Отсутствует candidate conformance: {CandidateConformancePath}");

        using var baselineContract = ReadJson(root, BaselineContractPath);
        using var baselineConformance = ReadJson(root, BaselineConformancePath);
        using var candidateContract = ReadJson(root, CandidateContractPath);
        using var candidateConformance = ReadJson(root, CandidateConformancePath);
        using var policy = ReadJson(root, "repo-policy.json");

        Assert.Equal("webassistant-contract/v0.3", RequiredString(candidateContract.RootElement, "schema"));
        Assert.Equal("candidate", RequiredString(candidateContract.RootElement, "status"));
        Assert.False(candidateContract.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(CandidateConformancePath, RequiredString(candidateContract.RootElement, "conformanceCorpus"));

        Assert.Equal("webassistant-conformance/v0.3", RequiredString(candidateConformance.RootElement, "schema"));
        Assert.Equal("candidate", RequiredString(candidateConformance.RootElement, "status"));
        Assert.False(candidateConformance.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            RequiredString(candidateContract.RootElement, "schema"),
            RequiredString(candidateConformance.RootElement, "contract"));

        AssertCurrentAuthorityRemainsV02(policy.RootElement);
        AssertBaselineRequirementsArePreserved(baselineContract.RootElement, candidateContract.RootElement);
        AssertBaselineVectorsArePreserved(baselineConformance.RootElement, candidateConformance.RootElement);

        var candidateRequirementIds = candidateContract.RootElement
            .GetProperty("requirements")
            .EnumerateArray()
            .Select(requirement => RequiredString(requirement, "id"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var requirementId in DistributionRequirementIds)
        {
            Assert.Contains(requirementId, candidateRequirementIds);
        }

        var candidateVectorIds = candidateConformance.RootElement
            .GetProperty("vectors")
            .EnumerateArray()
            .Select(vector => RequiredString(vector, "id"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var vectorId in DistributionVectorIds)
        {
            Assert.Contains(vectorId, candidateVectorIds);
        }

        var graphErrors = ConformanceGraphValidator.Validate(
            candidateContract.RootElement,
            candidateConformance.RootElement,
            path => File.Exists(ToFullPath(root, path)),
            path => File.Exists(ToFullPath(root, path)) ? File.ReadAllText(ToFullPath(root, path)) : null);

        Assert.True(
            graphErrors.Count == 0,
            "Candidate conformance graph invalid:\n" + string.Join("\n", graphErrors));
    }

    private static void AssertCurrentAuthorityRemainsV02(JsonElement policy)
    {
        var current = policy
            .GetProperty("contract_conformance")
            .GetProperty("current");

        Assert.Equal(
            BaselineContractPath,
            RequiredString(current.GetProperty("contract"), "path"));
        Assert.Equal(
            BaselineConformancePath,
            RequiredString(current.GetProperty("conformance"), "path"));
    }

    private static void AssertBaselineRequirementsArePreserved(JsonElement baseline, JsonElement candidate)
    {
        var candidateRequirements = candidate
            .GetProperty("requirements")
            .EnumerateArray()
            .ToDictionary(
                requirement => RequiredString(requirement, "id"),
                requirement => (
                    Kind: RequiredString(requirement, "kind"),
                    Statement: RequiredString(requirement, "statement")),
                StringComparer.Ordinal);

        foreach (var baselineRequirement in baseline.GetProperty("requirements").EnumerateArray())
        {
            var id = RequiredString(baselineRequirement, "id");
            Assert.True(candidateRequirements.TryGetValue(id, out var candidateRequirement), $"Candidate потерял requirement {id}");
            Assert.Equal(RequiredString(baselineRequirement, "kind"), candidateRequirement.Kind);
            Assert.Equal(RequiredString(baselineRequirement, "statement"), candidateRequirement.Statement);
        }
    }

    private static void AssertBaselineVectorsArePreserved(JsonElement baseline, JsonElement candidate)
    {
        var candidateVectors = candidate
            .GetProperty("vectors")
            .EnumerateArray()
            .ToDictionary(
                vector => RequiredString(vector, "id"),
                vector => (
                    Assertion: RequiredString(vector, "assertion"),
                    Requirements: ReadStringArray(vector, "requirements")),
                StringComparer.Ordinal);

        foreach (var baselineVector in baseline.GetProperty("vectors").EnumerateArray())
        {
            var id = RequiredString(baselineVector, "id");
            Assert.True(candidateVectors.TryGetValue(id, out var candidateVector), $"Candidate потерял conformance vector {id}");
            Assert.Equal(RequiredString(baselineVector, "assertion"), candidateVector.Assertion);
            Assert.Equal(ReadStringArray(baselineVector, "requirements"), candidateVector.Requirements);
        }
    }

    private static string[] ReadStringArray(JsonElement element, string property) =>
        element
            .GetProperty(property)
            .EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidOperationException($"{property} содержит non-string value"))
            .ToArray();

    private static JsonDocument ReadJson(string root, string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(ToFullPath(root, relativePath)));

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"Отсутствует строковое поле {property}");

    private static string ToFullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
