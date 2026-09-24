using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class DocumentationGovernanceTests
{
    private static readonly string[] ConfigurationSemanticSources =
    [
        "webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs",
        "webassist/src/WebAssistant/FileSystem/FileSystemRootRegistry.cs",
        "webassist/build/common/default-appsettings.json"
    ];

    private static readonly string[] RequiredConfigurationAuditTargets =
    [
        "webassist/docs/configuration.md",
        "webassist/docs/appsettings.schema.json",
        "webassist/README.md",
        "contracts/webassistant-contract-v0.3.json",
        "contracts/webassistant-conformance-v0.3.json"
    ];

    [Fact]
    public void Repository_DeclaresCurrentDocumentationMapAndConfigurationContract()
    {
        Assert.True(File.Exists(RepositoryPath("docs/documentation-governance.md")));
        Assert.True(File.Exists(RepositoryPath("webassist/docs/configuration.md")));
        Assert.True(File.Exists(RepositoryPath("webassist/docs/appsettings.schema.json")));

        var map = Read("docs/documentation-governance.md");
        Assert.Contains("Карта актуальной документации", map, StringComparison.Ordinal);
        Assert.Contains("docs/superpowers", map, StringComparison.Ordinal);
        Assert.Contains("историчес", map, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("webassist/docs/configuration.md", map, StringComparison.Ordinal);
        Assert.Contains("webassist/docs/api.md", map, StringComparison.Ordinal);
        Assert.Contains("contracts/webassistant-contract-v0.3.json", map, StringComparison.Ordinal);
        Assert.Contains("accepted", map, StringComparison.OrdinalIgnoreCase);

        var configuration = Read("webassist/docs/configuration.md");
        Assert.Contains("appsettings.json", configuration, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:Port", configuration, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:LogDirectory", configuration, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:Cors:Enabled", configuration, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:Cors:AllowedOrigins", configuration, StringComparison.Ordinal);
        Assert.Contains("WebAssistant:FileSystem", configuration, StringComparison.Ordinal);
        Assert.Contains("build/common/default-appsettings.json", configuration, StringComparison.Ordinal);
        Assert.Contains("environment", configuration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command", configuration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("неизвест", configuration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", configuration, StringComparison.Ordinal);
        Assert.Contains("Linux", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationSchema_TracksCanonicalSafeDefaultAndRuntimeBounds()
    {
        using var schema = JsonDocument.Parse(Read("webassist/docs/appsettings.schema.json"));
        using var defaults = JsonDocument.Parse(Read("webassist/build/common/default-appsettings.json"));

        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            schema.RootElement.GetProperty("$schema").GetString());

        var webAssistantSchema = schema.RootElement
            .GetProperty("properties")
            .GetProperty("WebAssistant")
            .GetProperty("properties");

        var port = webAssistantSchema.GetProperty("Port");
        Assert.Equal(1024, port.GetProperty("minimum").GetInt32());
        Assert.Equal(65535, port.GetProperty("maximum").GetInt32());
        Assert.Equal(17654, port.GetProperty("default").GetInt32());

        var fileSystem = webAssistantSchema.GetProperty("FileSystem");
        Assert.Equal("object", fileSystem.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            fileSystem.GetProperty("additionalProperties").GetProperty("type").GetString());

        var defaultWebAssistant = defaults.RootElement.GetProperty("WebAssistant");
        Assert.Equal(
            port.GetProperty("default").GetInt32(),
            defaultWebAssistant.GetProperty("Port").GetInt32());
        Assert.False(defaultWebAssistant.GetProperty("Cors").GetProperty("Enabled").GetBoolean());
        Assert.Empty(defaultWebAssistant.GetProperty("Cors").GetProperty("AllowedOrigins").EnumerateArray());
        Assert.Empty(defaultWebAssistant.GetProperty("FileSystem").EnumerateObject());
    }

    [Fact]
    public void ConfigurationSemanticChange_RequiresEveryCurrentConfigurationAuditTarget()
    {
        using var policy = JsonDocument.Parse(Read("repo-policy.json"));
        var rules = policy.RootElement.GetProperty("cochange_rules").EnumerateArray().ToArray();

        foreach (var target in RequiredConfigurationAuditTargets)
        {
            Assert.Contains(
                rules,
                rule =>
                {
                    var triggers = rule.GetProperty("if_changed")
                        .EnumerateArray()
                        .Select(value => value.GetString())
                        .ToArray();
                    var required = rule.GetProperty("must_change_any")
                        .EnumerateArray()
                        .Select(value => value.GetString())
                        .ToArray();

                    return ConfigurationSemanticSources.All(source => triggers.Contains(source, StringComparer.Ordinal))
                        && required.Length == 1
                        && string.Equals(required[0], target, StringComparison.Ordinal);
                });
        }

        var changed = new HashSet<string>(
            [ConfigurationSemanticSources[0], RequiredConfigurationAuditTargets[0]],
            StringComparer.Ordinal);
        var unsatisfied = rules
            .Where(rule => RuleTriggered(rule, changed) && !RuleSatisfied(rule, changed))
            .SelectMany(rule => rule.GetProperty("must_change_any").EnumerateArray())
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var target in RequiredConfigurationAuditTargets.Skip(1))
        {
            Assert.Contains(target, unsatisfied);
        }
    }

    [Fact]
    public void PullRequestTemplate_RequiresExplicitFullDocumentationAudit()
    {
        var template = Read(".github/PULL_REQUEST_TEMPLATE.md");

        Assert.Contains("Полный аудит документации", template, StringComparison.Ordinal);
        Assert.Contains("Проверены актуальные документы", template, StringComparison.Ordinal);
        Assert.Contains("Обновлены", template, StringComparison.Ordinal);
        Assert.Contains("Не затронуты", template, StringComparison.Ordinal);
        Assert.Contains("accepted immutable", template, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductReadme_UsesCurrentFilesystemMethodsAndLinksConfigurationReference()
    {
        var readme = Read("webassist/README.md");

        Assert.Contains("docs/configuration.md", readme, StringComparison.Ordinal);
        Assert.Contains("POST   /v1/filesystem/find", readme, StringComparison.Ordinal);
        Assert.Contains("POST   /v1/filesystem/file/delete", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("PUT    /v1/filesystem/file", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE /v1/filesystem/file", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE /v1/filesystem/directory", readme, StringComparison.Ordinal);
        Assert.Contains(
            "Публичная filesystem API использует только",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("`GET`", readme, StringComparison.Ordinal);
        Assert.Contains("`POST`", readme, StringComparison.Ordinal);
    }

    private static bool RuleTriggered(JsonElement rule, IReadOnlySet<string> changed) =>
        rule.GetProperty("if_changed")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .Any(changed.Contains);

    private static bool RuleSatisfied(JsonElement rule, IReadOnlySet<string> changed) =>
        rule.GetProperty("must_change_any")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .Any(changed.Contains);

    private static string Read(string relativePath) =>
        File.ReadAllText(RepositoryPath(relativePath));

    private static string RepositoryPath(string relativePath) =>
        Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "repo-policy.json")) &&
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
