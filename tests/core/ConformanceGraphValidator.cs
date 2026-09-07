using System.Text.Json;

namespace WebAssistant.CoreTests;

internal static class ConformanceGraphValidator
{
    private const string StableCiPath = ".github/workflows/ci.yml";
    private const string CoreWorkflowPath = ".github/workflows/core.yml";
    private const string WindowsServiceWorkflowPath = ".github/workflows/windows-service.yml";
    private const string LinuxSystemdWorkflowPath = ".github/workflows/linux-systemd.yml";

    private static readonly HashSet<string> NormativeRequirementKinds =
        new(["required", "boundary"], StringComparer.Ordinal);

    private static readonly HashSet<string> VectorKinds =
        new(["positive", "negative"], StringComparer.Ordinal);

    public static IReadOnlyList<string> ValidateCurrentRepository(string repositoryRoot)
    {
        var errors = new List<string>();

        try
        {
            var policyPath = Path.Combine(repositoryRoot, "repo-policy.json");
            if (!File.Exists(policyPath))
            {
                return ["Отсутствует repo-policy.json для разрешения current contract/conformance pair."];
            }

            using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
            var current = policy.RootElement
                .GetProperty("contract_conformance")
                .GetProperty("current");

            var contractRelativePath = NormalizeRepositoryPath(
                RequireString(current.GetProperty("contract"), "path", "current contract path"));
            var conformanceRelativePath = NormalizeRepositoryPath(
                RequireString(current.GetProperty("conformance"), "path", "current conformance path"));

            var contractPath = ToFullPath(repositoryRoot, contractRelativePath);
            var conformancePath = ToFullPath(repositoryRoot, conformanceRelativePath);

            if (!File.Exists(contractPath))
            {
                errors.Add($"Отсутствует current contract: {contractRelativePath}");
            }

            if (!File.Exists(conformancePath))
            {
                errors.Add($"Отсутствует current conformance: {conformanceRelativePath}");
            }

            if (errors.Count != 0)
            {
                return errors;
            }

            using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
            using var conformance = JsonDocument.Parse(File.ReadAllText(conformancePath));

            errors.AddRange(Validate(
                contract.RootElement,
                conformance.RootElement,
                path => File.Exists(ToFullPath(repositoryRoot, path)),
                path => ReadRepositoryText(repositoryRoot, path)));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"Невозможно проверить current conformance graph: {exception.Message}");
        }

        return errors;
    }

    public static IReadOnlyList<string> Validate(
        JsonElement contract,
        JsonElement conformance,
        Func<string, bool> pathExists,
        Func<string, string?> readText)
    {
        var errors = new List<string>();
        var requirements = new Dictionary<string, string>(StringComparer.Ordinal);
        var normativeRequirements = new HashSet<string>(StringComparer.Ordinal);
        var coveredRequirements = new HashSet<string>(StringComparer.Ordinal);
        var requiredRepositoryPaths = ReadRequiredRepositoryPaths(conformance, errors);

        if (!contract.TryGetProperty("requirements", out var requirementArray) ||
            requirementArray.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Contract не содержит массив requirements.");
            return errors;
        }

        foreach (var requirement in requirementArray.EnumerateArray())
        {
            var id = OptionalString(requirement, "id");
            var kind = OptionalString(requirement, "kind");

            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("Contract requirement без id.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(kind) || !NormativeRequirementKinds.Contains(kind))
            {
                errors.Add($"Requirement {id}: неизвестный нормативный kind '{kind ?? "<missing>"}'.");
                continue;
            }

            if (!requirements.TryAdd(id, kind))
            {
                errors.Add($"Дублирующий requirement id: {id}");
                continue;
            }

            normativeRequirements.Add(id);
        }

        if (!conformance.TryGetProperty("vectors", out var vectorArray) ||
            vectorArray.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Conformance не содержит массив vectors.");
            return errors;
        }

        var vectorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in vectorArray.EnumerateArray())
        {
            var vectorId = OptionalString(vector, "id") ?? "<missing-vector-id>";
            if (!vectorIds.Add(vectorId))
            {
                errors.Add($"Дублирующий conformance vector id: {vectorId}");
            }

            var vectorKind = OptionalString(vector, "kind");
            if (string.IsNullOrWhiteSpace(vectorKind) || !VectorKinds.Contains(vectorKind))
            {
                errors.Add($"Vector {vectorId}: неизвестный kind '{vectorKind ?? "<missing>"}'.");
            }

            var assertion = OptionalString(vector, "assertion");
            if (string.IsNullOrWhiteSpace(assertion))
            {
                errors.Add($"Vector {vectorId}: отсутствует assertion.");
            }

            ValidateRequirementReferences(vector, vectorId, requirements, coveredRequirements, errors);
            ValidateEvidence(
                vector,
                vectorId,
                requiredRepositoryPaths,
                pathExists,
                readText,
                errors);
        }

        foreach (var requirementId in normativeRequirements.Order(StringComparer.Ordinal))
        {
            if (!coveredRequirements.Contains(requirementId))
            {
                errors.Add($"Нормативное требование {requirementId} не покрыто ни одним conformance vector.");
            }
        }

        return errors;
    }

    private static HashSet<string>? ReadRequiredRepositoryPaths(JsonElement conformance, List<string> errors)
    {
        if (!conformance.TryGetProperty("requiredRepositoryPaths", out var paths))
        {
            return null;
        }

        if (paths.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Conformance requiredRepositoryPaths должен быть массивом.");
            return null;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in paths.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                errors.Add("Conformance requiredRepositoryPaths содержит некорректный путь.");
                continue;
            }

            result.Add(NormalizeRepositoryPath(item.GetString()!));
        }

        return result;
    }

    private static void ValidateRequirementReferences(
        JsonElement vector,
        string vectorId,
        IReadOnlyDictionary<string, string> requirements,
        ISet<string> coveredRequirements,
        List<string> errors)
    {
        if (!vector.TryGetProperty("requirements", out var references) ||
            references.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Vector {vectorId}: отсутствует массив requirements.");
            return;
        }

        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reference.GetString()))
            {
                errors.Add($"Vector {vectorId}: некорректная ссылка на requirement.");
                continue;
            }

            var requirementId = reference.GetString()!;
            if (!requirements.ContainsKey(requirementId))
            {
                errors.Add($"Vector {vectorId}: неизвестное требование {requirementId}.");
                continue;
            }

            coveredRequirements.Add(requirementId);
        }
    }

    private static void ValidateEvidence(
        JsonElement vector,
        string vectorId,
        HashSet<string>? requiredRepositoryPaths,
        Func<string, bool> pathExists,
        Func<string, string?> readText,
        List<string> errors)
    {
        if (!vector.TryGetProperty("evidence", out var evidenceArray) ||
            evidenceArray.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Vector {vectorId}: отсутствует массив evidence.");
            return;
        }

        if (evidenceArray.GetArrayLength() == 0)
        {
            errors.Add($"Vector {vectorId}: evidence не может быть пустым.");
            return;
        }

        foreach (var evidence in evidenceArray.EnumerateArray())
        {
            if (evidence.ValueKind == JsonValueKind.String)
            {
                var path = NormalizeRepositoryPath(evidence.GetString() ?? string.Empty);
                ValidateRepositoryEvidence(
                    vectorId,
                    path,
                    requiredRepositoryPaths,
                    pathExists,
                    readText,
                    errors);
                continue;
            }

            if (evidence.ValueKind == JsonValueKind.Object)
            {
                ValidateExplicitEvidence(vectorId, evidence, pathExists, errors);
                continue;
            }

            errors.Add($"Vector {vectorId}: неизвестный тип evidence JSON {evidence.ValueKind}.");
        }
    }

    private static void ValidateRepositoryEvidence(
        string vectorId,
        string path,
        HashSet<string>? requiredRepositoryPaths,
        Func<string, bool> pathExists,
        Func<string, string?> readText,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"Vector {vectorId}: пустой evidence path.");
            return;
        }

        if (!pathExists(path))
        {
            errors.Add($"Vector {vectorId}: отсутствует evidence path {path}.");
        }

        if (requiredRepositoryPaths is not null && !requiredRepositoryPaths.Contains(path))
        {
            errors.Add($"Vector {vectorId}: evidence path {path} не объявлен в requiredRepositoryPaths.");
        }

        var kind = ClassifyRepositoryEvidence(path);
        if (kind == EvidenceKind.Unknown)
        {
            errors.Add($"Vector {vectorId}: неизвестный тип evidence для {path}.");
            return;
        }

        ValidateAutomatedBinding(vectorId, path, kind, readText, errors);
    }

    private static void ValidateExplicitEvidence(
        string vectorId,
        JsonElement evidence,
        Func<string, bool> pathExists,
        List<string> errors)
    {
        var kind = OptionalString(evidence, "kind");
        if (!string.Equals(kind, "physical_manual", StringComparison.Ordinal))
        {
            errors.Add($"Vector {vectorId}: неизвестный тип evidence '{kind ?? "<missing>"}'.");
            return;
        }

        var artifact = OptionalString(evidence, "artifact");
        if (string.IsNullOrWhiteSpace(artifact))
        {
            errors.Add($"Vector {vectorId}: physical/manual evidence не содержит artifact.");
            return;
        }

        var path = NormalizeRepositoryPath(artifact);
        if (!pathExists(path))
        {
            errors.Add($"Vector {vectorId}: отсутствует evidence path {path}.");
        }

        var accepted = evidence.TryGetProperty("accepted", out var acceptedElement) &&
                       acceptedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                       acceptedElement.GetBoolean();
        if (!accepted)
        {
            errors.Add($"Vector {vectorId}: physical/manual evidence {path} не принят явным accepted fact.");
        }
    }

    private static void ValidateAutomatedBinding(
        string vectorId,
        string path,
        EvidenceKind kind,
        Func<string, string?> readText,
        List<string> errors)
    {
        switch (kind)
        {
            case EvidenceKind.CoreTest:
                RequireWorkflowBinding(
                    vectorId,
                    path,
                    CoreWorkflowPath,
                    "dotnet test tests/core/WebAssistant.CoreTests.csproj",
                    readText,
                    errors);
                break;

            case EvidenceKind.Workflow:
                if (!string.Equals(path, StableCiPath, StringComparison.Ordinal))
                {
                    RequireStableCiUsesWorkflow(vectorId, path, path, readText, errors);
                }
                break;

            case EvidenceKind.WindowsServiceScript:
                RequireWorkflowBinding(
                    vectorId,
                    path,
                    WindowsServiceWorkflowPath,
                    path,
                    readText,
                    errors);
                break;

            case EvidenceKind.LinuxSystemdScript:
                RequireWorkflowBinding(
                    vectorId,
                    path,
                    LinuxSystemdWorkflowPath,
                    path,
                    readText,
                    errors);
                break;
        }
    }

    private static void RequireWorkflowBinding(
        string vectorId,
        string evidencePath,
        string workflowPath,
        string requiredWorkflowText,
        Func<string, string?> readText,
        List<string> errors)
    {
        var workflow = readText(workflowPath);
        if (workflow is null ||
            !NormalizeTextPath(workflow).Contains(NormalizeTextPath(requiredWorkflowText), StringComparison.Ordinal))
        {
            errors.Add($"Vector {vectorId}: automated evidence {evidencePath} не входит в workflow {workflowPath}.");
            return;
        }

        RequireStableCiUsesWorkflow(vectorId, evidencePath, workflowPath, readText, errors);
    }

    private static void RequireStableCiUsesWorkflow(
        string vectorId,
        string evidencePath,
        string workflowPath,
        Func<string, string?> readText,
        List<string> errors)
    {
        var stableCi = readText(StableCiPath);
        var expectedUse = $"uses: ./{workflowPath}";
        if (stableCi is null || !NormalizeTextPath(stableCi).Contains(NormalizeTextPath(expectedUse), StringComparison.Ordinal))
        {
            errors.Add($"Vector {vectorId}: automated evidence {evidencePath} не входит в stable CI через {workflowPath}.");
        }
    }

    private static EvidenceKind ClassifyRepositoryEvidence(string path)
    {
        if (path.StartsWith("tests/core/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.CoreTest;
        }

        if (path.StartsWith(".github/workflows/", StringComparison.Ordinal) &&
            (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return EvidenceKind.Workflow;
        }

        if (path.StartsWith("tests/windows-service/", StringComparison.Ordinal) && path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.WindowsServiceScript;
        }

        if (path.StartsWith("tests/linux-systemd/", StringComparison.Ordinal) && path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.LinuxSystemdScript;
        }

        if (path.Equals("README.md", StringComparison.Ordinal) ||
            path.Equals("webassist/README.md", StringComparison.Ordinal) ||
            path.StartsWith("webassist/docs/", StringComparison.Ordinal))
        {
            return EvidenceKind.Documentation;
        }

        if (path.Equals("webassist/vendor/naps2/README.md", StringComparison.Ordinal))
        {
            return EvidenceKind.DependencyProvenance;
        }

        if (path.StartsWith("webassist/vendor/nuget/", StringComparison.Ordinal) && path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.DependencyArtifact;
        }

        if (path.Equals("webassist/.gitlab-ci.yml", StringComparison.Ordinal))
        {
            return EvidenceKind.ProductCi;
        }

        if (path.Equals("webassist/NuGet.Config", StringComparison.Ordinal) ||
            path.EndsWith("/appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.Configuration;
        }

        if (path.StartsWith("webassist/src/", StringComparison.Ordinal) &&
            (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            return EvidenceKind.ProductImplementation;
        }

        if (path.StartsWith("webassist/build/", StringComparison.Ordinal) ||
            path.StartsWith("webassist/install/", StringComparison.Ordinal))
        {
            return EvidenceKind.ProductEntrypoint;
        }

        return EvidenceKind.Unknown;
    }

    private static string? ReadRepositoryText(string repositoryRoot, string relativePath)
    {
        var fullPath = ToFullPath(repositoryRoot, relativePath);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    private static string ToFullPath(string repositoryRoot, string relativePath) =>
        Path.Combine(
            repositoryRoot,
            NormalizeRepositoryPath(relativePath).Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeRepositoryPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string NormalizeTextPath(string text) => text.Replace('\\', '/');

    private static string RequireString(JsonElement element, string property, string description)
    {
        var value = OptionalString(element, property);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Отсутствует {description}.");
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private enum EvidenceKind
    {
        Unknown,
        CoreTest,
        Workflow,
        WindowsServiceScript,
        LinuxSystemdScript,
        Documentation,
        ProductCi,
        Configuration,
        ProductImplementation,
        ProductEntrypoint,
        DependencyArtifact,
        DependencyProvenance
    }
}
