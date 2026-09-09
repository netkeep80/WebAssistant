using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class InstallationGuideInfrastructureTests
{
    private static readonly string[] RequiredCaptureSlots =
    [
        "WIN_ARTIFACT_IDENTITY",
        "WIN_INSTALL_UAC",
        "WIN_INSTALLED_APPS",
        "WIN_SERVICE_HEALTH",
        "WIN_UNINSTALL",
        "ALT_ARTIFACT_IDENTITY",
        "ALT_INSTALL",
        "ALT_SYSTEMD_HEALTH",
        "ALT_RESTART",
        "ALT_UNINSTALL"
    ];

    [Fact]
    public void ProductRoot_DefinesEditableGuideEvidenceSchemaAndRequiredCaptureSlots()
    {
        var root = FindRepositoryRoot();
        var docsRoot = Path.Combine(root, "webassist", "docs");
        var guidePath = Path.Combine(docsRoot, "installation-guide.md");
        var infrastructureRoot = Path.Combine(docsRoot, "installation-guide");
        var schemaPath = Path.Combine(infrastructureRoot, "evidence.schema.json");

        Assert.True(File.Exists(guidePath), $"Отсутствует editable guide authority: {guidePath}");
        Assert.True(File.Exists(schemaPath), $"Отсутствует evidence schema: {schemaPath}");

        var guide = File.ReadAllText(guidePath);
        foreach (var slot in RequiredCaptureSlots)
        {
            Assert.Contains($"{{{{{slot}}}}}", guide, StringComparison.Ordinal);
        }

        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var rootElement = schema.RootElement;
        Assert.Equal("webassistant-installation-evidence/v1", rootElement.GetProperty("$id").GetString());
        Assert.Equal("object", rootElement.GetProperty("type").GetString());

        var required = rootElement.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var property in new[] { "schema", "kind", "sourceSha", "version", "artifacts", "altTarget", "captures" })
        {
            Assert.Contains(property, required);
        }
    }

    [Fact]
    public void ProductRoot_DefinesPinnedFailClosedFixtureAndFinalPdfBuildInterface()
    {
        var root = FindRepositoryRoot();
        var infrastructureRoot = Path.Combine(root, "webassist", "docs", "installation-guide");
        var toolchainPath = Path.Combine(infrastructureRoot, "toolchain.env");
        var buildPath = Path.Combine(infrastructureRoot, "build.sh");
        var verifyPath = Path.Combine(infrastructureRoot, "verify.sh");

        Assert.True(File.Exists(toolchainPath), $"Отсутствует pinned document toolchain: {toolchainPath}");
        Assert.True(File.Exists(buildPath), $"Отсутствует PDF build entrypoint: {buildPath}");
        Assert.True(File.Exists(verifyPath), $"Отсутствует PDF verification entrypoint: {verifyPath}");

        var toolchain = File.ReadAllText(toolchainPath);
        Assert.Contains("sha256:", toolchain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latest", toolchain, StringComparison.OrdinalIgnoreCase);

        var build = File.ReadAllText(buildPath);
        Assert.Contains("--mode", build, StringComparison.Ordinal);
        Assert.Contains("fixture", build, StringComparison.Ordinal);
        Assert.Contains("final", build, StringComparison.Ordinal);
        Assert.Contains("WEBASSISTANT_SOURCE_SHA", build, StringComparison.Ordinal);
        Assert.Contains("evidence.schema.json", build, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-Installation-Guide.pdf", build, StringComparison.Ordinal);
        Assert.Contains("artifacts", build, StringComparison.Ordinal);

        var verify = File.ReadAllText(verifyPath);
        Assert.Contains("WebAssistant-Installation-Guide.pdf", verify, StringComparison.Ordinal);
        Assert.Contains("render", verify, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page", verify, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateConformance_InstallGuideVectorReferencesExecutableInfrastructureEvidence()
    {
        var root = FindRepositoryRoot();
        var conformancePath = Path.Combine(root, "contracts", "webassistant-conformance-v0.3.json");
        using var conformance = JsonDocument.Parse(File.ReadAllText(conformancePath));

        Assert.Equal("candidate", conformance.RootElement.GetProperty("status").GetString());
        Assert.False(conformance.RootElement.GetProperty("accepted").GetBoolean());

        var vector = conformance.RootElement
            .GetProperty("vectors")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "WA-C-INSTALL-GUIDE-001");

        var evidence = vector.GetProperty("evidence")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => item is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("tests/core/InstallationGuideInfrastructureTests.cs", evidence);
        Assert.Contains("webassist/docs/installation-guide.md", evidence);
        Assert.Contains("webassist/docs/installation-guide/evidence.schema.json", evidence);
        Assert.Contains("webassist/docs/installation-guide/build.sh", evidence);
        Assert.Contains("webassist/docs/installation-guide/verify.sh", evidence);
    }

    [Fact]
    public void ProductReadme_ReferencesCanonicalInstallationGuideWithoutParentRepositoryDependency()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "webassist", "README.md"));

        Assert.Contains("docs/installation-guide.md", readme, StringComparison.Ordinal);
        Assert.Contains("WebAssistant-Installation-Guide.pdf", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("../docs/installation-guide", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                Directory.Exists(Path.Combine(directory.FullName, "contracts")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
