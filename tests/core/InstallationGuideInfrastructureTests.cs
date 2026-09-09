using System.Diagnostics;
using System.Security.Cryptography;
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
        Assert.False(rootElement.GetProperty("additionalProperties").GetBoolean());

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
        Assert.Contains("PANDOC_VERSION=", toolchain, StringComparison.Ordinal);
        Assert.Contains("WEASYPRINT_VERSION=", toolchain, StringComparison.Ordinal);
        Assert.Contains("POPPLER_VERSION=", toolchain, StringComparison.Ordinal);
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
        Assert.Contains("pandoc", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weasyprint", build, StringComparison.OrdinalIgnoreCase);

        var verify = File.ReadAllText(verifyPath);
        Assert.Contains("WebAssistant-Installation-Guide.pdf", verify, StringComparison.Ordinal);
        Assert.Contains("pdftoppm", verify, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pdfinfo", verify, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page", verify, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceValidator_AcceptsCompleteFixtureAndExpandsGuide()
    {
        var fixture = CreateEvidenceFixture(RequiredCaptureSlots);
        try
        {
            var output = Path.Combine(fixture.Root, "expanded.md");
            var result = RunEvidenceTool(fixture, "fixture", fixture.SourceSha, output);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(output));
            var expanded = File.ReadAllText(output);
            Assert.DoesNotContain("{{WIN_ARTIFACT_IDENTITY}}", expanded, StringComparison.Ordinal);
            Assert.DoesNotContain("{{ALT_UNINSTALL}}", expanded, StringComparison.Ordinal);
            Assert.Contains($"WebAssistant-win-x64-{fixture.Version}.exe", expanded, StringComparison.Ordinal);
            Assert.Contains($"WebAssistant-linux-x64-{fixture.Version}.zip", expanded, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public void EvidenceValidator_RejectsSourceIdentityMismatch()
    {
        var fixture = CreateEvidenceFixture(RequiredCaptureSlots);
        try
        {
            var result = RunEvidenceTool(fixture, "fixture", new string('b', 40), Path.Combine(fixture.Root, "expanded.md"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("sourceSha", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public void EvidenceValidator_RejectsMissingRequiredCaptureSlot()
    {
        var fixture = CreateEvidenceFixture(RequiredCaptureSlots[..^1]);
        try
        {
            var result = RunEvidenceTool(fixture, "fixture", fixture.SourceSha, Path.Combine(fixture.Root, "expanded.md"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("missing required capture slots", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public void EvidenceValidator_RejectsCaptureHashMismatch()
    {
        var fixture = CreateEvidenceFixture(RequiredCaptureSlots);
        try
        {
            File.AppendAllText(fixture.FirstCapture, "tampered");
            var result = RunEvidenceTool(fixture, "fixture", fixture.SourceSha, Path.Combine(fixture.Root, "expanded.md"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("sha256 mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public void EvidenceValidator_RejectsFixtureEvidenceInFinalMode()
    {
        var fixture = CreateEvidenceFixture(RequiredCaptureSlots);
        try
        {
            var result = RunEvidenceTool(fixture, "final", fixture.SourceSha, Path.Combine(fixture.Root, "expanded.md"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("cannot be used in mode=final", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
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
        Assert.Contains("webassist/docs/installation-guide/evidence.py", evidence);
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

    private static EvidenceFixture CreateEvidenceFixture(IEnumerable<string> slots)
    {
        var root = Directory.CreateTempSubdirectory("webassistant-guide-evidence-").FullName;
        var sourceSha = new string('a', 40);
        var repositoryRoot = FindRepositoryRoot();
        var version = File.ReadAllText(Path.Combine(repositoryRoot, "webassist", "VERSION")).Trim();
        var captures = new List<object>();
        string? firstCapture = null;

        foreach (var slot in slots)
        {
            var fileName = slot + ".txt";
            var path = Path.Combine(root, fileName);
            File.WriteAllText(path, $"fixture {slot}\n");
            firstCapture ??= path;
            captures.Add(new
            {
                slot,
                platform = slot.StartsWith("WIN_", StringComparison.Ordinal) ? "windows" : "alt-linux",
                path = fileName,
                sha256 = Sha256(path),
                kind = "fixture"
            });
        }

        var manifest = new
        {
            schema = "webassistant-installation-evidence/v1",
            kind = "fixture",
            sourceSha,
            version,
            artifacts = new
            {
                windows = new { filename = $"WebAssistant-win-x64-{version}.exe", sha256 = new string('1', 64) },
                linux = new { filename = $"WebAssistant-linux-x64-{version}.zip", sha256 = new string('2', 64) }
            },
            altTarget = new { osName = "fixture", osVersion = "fixture", completed = false },
            captures
        };
        var manifestPath = Path.Combine(root, "evidence.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        return new EvidenceFixture(root, manifestPath, firstCapture ?? Path.Combine(root, "missing.txt"), sourceSha, version);
    }

    private static ProcessResult RunEvidenceTool(EvidenceFixture fixture, string mode, string sourceSha, string output)
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = Path.Combine(repositoryRoot, "webassist", "docs", "installation-guide", "evidence.py");
        var guide = Path.Combine(repositoryRoot, "webassist", "docs", "installation-guide.md");
        Assert.True(File.Exists(script), $"Отсутствует evidence validator: {script}");

        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     script,
                     "--mode", mode,
                     "--manifest", fixture.Manifest,
                     "--guide", guide,
                     "--output-markdown", output,
                     "--version", fixture.Version,
                     "--source-sha", sourceSha
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить evidence validator.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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

    private sealed record EvidenceFixture(string Root, string Manifest, string FirstCapture, string SourceSha, string Version);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
