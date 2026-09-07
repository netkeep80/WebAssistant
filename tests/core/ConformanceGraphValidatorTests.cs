using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ConformanceGraphValidatorTests
{
    [Fact]
    public void CurrentAcceptedPair_HasCompleteEvidenceGraph()
    {
        var errors = ConformanceGraphValidator.ValidateCurrentRepository(FindRepositoryRoot());

        Assert.Empty(errors);
    }

    [Fact]
    public void OrphanNormativeRequirement_IsRejected()
    {
        var errors = ValidateFixture(
            requirements: """
                [
                  { "id": "WA-001", "kind": "required", "statement": "one" },
                  { "id": "WA-002", "kind": "required", "statement": "two" }
                ]
                """,
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[\"tests/core/EvidenceTests.cs\"]");

        Assert.Contains(errors, error => error.Contains("WA-002", StringComparison.Ordinal) && error.Contains("не покрыто", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DanglingRequirementReference_IsRejected()
    {
        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-UNKNOWN\"]",
            evidence: "[\"tests/core/EvidenceTests.cs\"]");

        Assert.Contains(errors, error => error.Contains("WA-UNKNOWN", StringComparison.Ordinal) && error.Contains("неизвестное требование", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingEvidencePath_IsRejected()
    {
        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[\"tests/core/MissingEvidenceTests.cs\"]");

        Assert.Contains(errors, error => error.Contains("MissingEvidenceTests.cs", StringComparison.Ordinal) && error.Contains("отсутствует evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownEvidenceClassification_IsRejected()
    {
        var files = CreateFiles();
        files["evidence/mystery.bin"] = "opaque";

        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[\"evidence/mystery.bin\"]",
            files: files);

        Assert.Contains(errors, error => error.Contains("mystery.bin", StringComparison.Ordinal) && error.Contains("неизвестный тип evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AutomatedEvidenceOutsideStableCi_IsRejected()
    {
        var files = CreateFiles();
        files[".github/workflows/ci.yml"] = "name: ci\njobs: {}\n";

        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[\"tests/core/EvidenceTests.cs\"]",
            files: files);

        Assert.Contains(errors, error => error.Contains("EvidenceTests.cs", StringComparison.Ordinal) && error.Contains("stable CI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalManualEvidenceWithoutAcceptedFact_IsRejected()
    {
        var files = CreateFiles();
        files["evidence/physical/alt-usb.md"] = "physical observation";

        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[{ \"kind\": \"physical_manual\", \"artifact\": \"evidence/physical/alt-usb.md\", \"accepted\": false }]",
            files: files);

        Assert.Contains(errors, error => error.Contains("physical/manual evidence", StringComparison.OrdinalIgnoreCase) && error.Contains("не принят", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AcceptedPhysicalManualEvidence_IsExplicitlyModeled()
    {
        var files = CreateFiles();
        files["evidence/physical/alt-usb.md"] = "physical observation";

        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[{ \"kind\": \"physical_manual\", \"artifact\": \"evidence/physical/alt-usb.md\", \"accepted\": true }]",
            files: files);

        Assert.Empty(errors);
    }

    [Fact]
    public void UnknownExplicitEvidenceKind_IsRejected()
    {
        var files = CreateFiles();
        files["evidence/physical/alt-usb.md"] = "physical observation";

        var errors = ValidateFixture(
            requirements: "[{ \"id\": \"WA-001\", \"kind\": \"required\", \"statement\": \"one\" }]",
            vectorRequirements: "[\"WA-001\"]",
            evidence: "[{ \"kind\": \"magic\", \"artifact\": \"evidence/physical/alt-usb.md\", \"accepted\": true }]",
            files: files);

        Assert.Contains(errors, error => error.Contains("magic", StringComparison.Ordinal) && error.Contains("неизвестный тип evidence", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ValidateFixture(
        string requirements,
        string vectorRequirements,
        string evidence,
        Dictionary<string, string>? files = null)
    {
        using var contract = JsonDocument.Parse($$"""
            {
              "requirements": {{requirements}}
            }
            """);
        using var conformance = JsonDocument.Parse($$"""
            {
              "vectors": [
                {
                  "id": "V-001",
                  "kind": "positive",
                  "assertion": "fixture assertion",
                  "requirements": {{vectorRequirements}},
                  "evidence": {{evidence}}
                }
              ]
            }
            """);

        var repositoryFiles = files ?? CreateFiles();
        return ConformanceGraphValidator.Validate(
            contract.RootElement,
            conformance.RootElement,
            path => repositoryFiles.ContainsKey(Normalize(path)),
            path => repositoryFiles.TryGetValue(Normalize(path), out var content) ? content : null);
    }

    private static Dictionary<string, string> CreateFiles() => new(StringComparer.Ordinal)
    {
        ["tests/core/EvidenceTests.cs"] = "namespace Fixture;",
        [".github/workflows/core.yml"] = "run: dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release\n",
        [".github/workflows/ci.yml"] = "uses: ./.github/workflows/core.yml\n"
    };

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

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
