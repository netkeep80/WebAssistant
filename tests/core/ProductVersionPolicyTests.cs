using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ProductVersionPolicyTests
{
    [Fact]
    public void RepoPolicy_RequiresStrictBaseToHeadProductVersionIncrease()
    {
        using var policy = JsonDocument.Parse(ReadRepositoryFile("repo-policy.json"));
        var root = policy.RootElement;

        Assert.True(
            root.TryGetProperty("document_relations", out var relations),
            "repo-policy.json must define document_relations for persisted product version monotonicity");

        var documents = relations.GetProperty("documents");
        AssertVersionDocument(documents.GetProperty("base-product-version"), "base");
        AssertVersionDocument(documents.GetProperty("head-product-version"), "head");

        var rule = relations
            .GetProperty("rules")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == "product-version-monotonic");

        Assert.Equal("scalar_strictly_greater", rule.GetProperty("kind").GetString());
        Assert.Equal("semver", rule.GetProperty("comparator").GetString());
        AssertSelector(rule.GetProperty("left"), "head-product-version");
        AssertSelector(rule.GetProperty("right"), "base-product-version");
    }

    private static void AssertVersionDocument(JsonElement document, string snapshot)
    {
        Assert.Equal("webassist/VERSION", document.GetProperty("path").GetString());
        Assert.Equal("plain_text", document.GetProperty("format").GetString());
        Assert.Equal(snapshot, document.GetProperty("snapshot").GetString());
    }

    private static void AssertSelector(JsonElement selector, string document)
    {
        Assert.Equal(document, selector.GetProperty("document").GetString());
        Assert.Equal(string.Empty, selector.GetProperty("pointer").GetString());
        Assert.Equal("string", selector.GetProperty("type").GetString());
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "webassist", "WebAssistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("WebAssistant repository root was not found.");
    }
}
