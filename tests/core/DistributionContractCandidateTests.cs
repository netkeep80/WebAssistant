using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DistributionContractCandidateTests
{
    private const string BaselineContractPath = "contracts/webassistant-contract-v0.2.json";
    private const string BaselineConformancePath = "contracts/webassistant-conformance-v0.2.json";
    private const string CandidateContractPath = "contracts/webassistant-contract-v0.3.json";
    private const string CandidateConformancePath = "contracts/webassistant-conformance-v0.3.json";
    private const string PatchedSdkPath = "webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg";
    private const string SupersededSdkPath = "webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.1.450cba65.nupkg";

    private static readonly HashSet<string> SupersededRequirementIds =
    [
        "WA-SCAN-001",
        "WA-SCAN-002"
    ];

    private static readonly HashSet<string> SupersededVectorIds =
    [
        "WA-C-SCANNER-HTTP-001"
    ];

    private static readonly string[] ScannerVectorIds =
    [
        "WA-C-SCANNER-DISCOVERY-001",
        "WA-C-SCANNER-HTTP-001",
        "WA-C-SCANNER-AUTO-001",
        "WA-C-SCANNER-STATELESS-001"
    ];

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
    public void DistributionCandidate_PreservesV02ExceptExplicitScannerSupersession()
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
        AssertBaselineRequirementsArePreservedExceptExplicitSupersession(
            baselineContract.RootElement,
            candidateContract.RootElement);
        AssertBaselineVectorsArePreservedExceptExplicitSupersession(
            baselineConformance.RootElement,
            candidateConformance.RootElement);

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

    [Fact]
    public void CandidateScannerContract_AuthorizesUnifiedStableAutoModel()
    {
        var root = FindRepositoryRoot();
        using var candidateContract = ReadJson(root, CandidateContractPath);
        var statements = candidateContract.RootElement
            .GetProperty("requirements")
            .EnumerateArray()
            .Select(requirement => RequiredString(requirement, "statement"))
            .ToArray();

        Assert.Contains(statements, statement =>
            statement.Contains("WIA", StringComparison.Ordinal) &&
            statement.Contains("TWAIN", StringComparison.Ordinal) &&
            statement.Contains("scannerId", StringComparison.Ordinal));

        Assert.Contains(statements, statement =>
            statement.Contains("POST /v1/scan", StringComparison.Ordinal) &&
            statement.Contains("source", StringComparison.Ordinal) &&
            statement.Contains("duplex", StringComparison.Ordinal));

        Assert.Contains(statements, statement =>
            statement.Contains("PRESENT", StringComparison.Ordinal) &&
            statement.Contains("ABSENT", StringComparison.Ordinal) &&
            statement.Contains("UNKNOWN", StringComparison.Ordinal));

        Assert.DoesNotContain(statements, statement =>
            statement.Contains("/v1/scan/feeder", StringComparison.Ordinal) ||
            statement.Contains("/v1/scan/duplex", StringComparison.Ordinal));
    }

    [Fact]
    public void CandidateScannerConformance_TracksPatchedSdkAndCurrentVectors()
    {
        var root = FindRepositoryRoot();
        using var candidateConformance = ReadJson(root, CandidateConformancePath);

        var requiredPaths = ReadStringArray(candidateConformance.RootElement, "requiredRepositoryPaths")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(PatchedSdkPath, requiredPaths);
        Assert.DoesNotContain(SupersededSdkPath, requiredPaths);
        Assert.Contains("tests/core/ScannerIdentityTests.cs", requiredPaths);
        Assert.Contains("tests/core/ScanSourcePolicyTests.cs", requiredPaths);
        Assert.Contains("tests/core/LinuxVirtualScanAdapterTests.cs", requiredPaths);
        Assert.Contains("tests/core/ServicePanelScannerApiTests.cs", requiredPaths);

        var vectors = candidateConformance.RootElement
            .GetProperty("vectors")
            .EnumerateArray()
            .ToDictionary(vector => RequiredString(vector, "id"), StringComparer.Ordinal);

        foreach (var vectorId in ScannerVectorIds)
        {
            Assert.Contains(vectorId, vectors.Keys);
        }

        Assert.Contains("WIA", RequiredString(vectors["WA-C-SCANNER-DISCOVERY-001"], "assertion"), StringComparison.Ordinal);
        Assert.Contains("TWAIN", RequiredString(vectors["WA-C-SCANNER-DISCOVERY-001"], "assertion"), StringComparison.Ordinal);
        Assert.Contains("POST /v1/scan", RequiredString(vectors["WA-C-SCANNER-HTTP-001"], "assertion"), StringComparison.Ordinal);
        Assert.Contains("PRESENT", RequiredString(vectors["WA-C-SCANNER-AUTO-001"], "assertion"), StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", RequiredString(vectors["WA-C-SCANNER-AUTO-001"], "assertion"), StringComparison.Ordinal);
        Assert.Contains("scannerId", RequiredString(vectors["WA-C-SCANNER-STATELESS-001"], "assertion"), StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateApiDocumentation_DescribesUnifiedScannerModel()
    {
        var root = FindRepositoryRoot();
        var api = File.ReadAllText(ToFullPath(root, "webassist/docs/api.md"));

        Assert.Contains("GET /v1/scanners", api, StringComparison.Ordinal);
        Assert.Contains("scannerId", api, StringComparison.Ordinal);
        Assert.Contains("warnings", api, StringComparison.Ordinal);
        Assert.Contains("WIA", api, StringComparison.Ordinal);
        Assert.Contains("TWAIN", api, StringComparison.Ordinal);
        Assert.Contains("POST /v1/scan", api, StringComparison.Ordinal);
        Assert.Contains("auto", api, StringComparison.Ordinal);
        Assert.Contains("flatbed", api, StringComparison.Ordinal);
        Assert.Contains("feeder", api, StringComparison.Ordinal);
        Assert.Contains("duplex", api, StringComparison.Ordinal);
        Assert.Contains("PRESENT", api, StringComparison.Ordinal);
        Assert.Contains("ABSENT", api, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", api, StringComparison.Ordinal);
        Assert.DoesNotContain("WIA-first", api, StringComparison.Ordinal);
        Assert.DoesNotContain("`POST /v1/scan/feeder`", api, StringComparison.Ordinal);
        Assert.DoesNotContain("`POST /v1/scan/duplex`", api, StringComparison.Ordinal);
        Assert.Contains("Source-specific routes", api, StringComparison.Ordinal);
        Assert.Contains("отсутствуют", api, StringComparison.Ordinal);
        Assert.DoesNotContain("Опциональный query parameter", api, StringComparison.Ordinal);
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

    private static void AssertBaselineRequirementsArePreservedExceptExplicitSupersession(
        JsonElement baseline,
        JsonElement candidate)
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

            if (SupersededRequirementIds.Contains(id))
            {
                continue;
            }

            Assert.Equal(RequiredString(baselineRequirement, "kind"), candidateRequirement.Kind);
            Assert.Equal(RequiredString(baselineRequirement, "statement"), candidateRequirement.Statement);
        }
    }

    private static void AssertBaselineVectorsArePreservedExceptExplicitSupersession(
        JsonElement baseline,
        JsonElement candidate)
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

            if (SupersededVectorIds.Contains(id))
            {
                continue;
            }

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
