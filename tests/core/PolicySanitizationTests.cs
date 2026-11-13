using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class PolicySanitizationTests
{
    [Fact]
    public void PublicPolicy_DoesNotEmbedExactIdentityDenylist()
    {
        var policyPath = Path.Combine(FindRepositoryRoot(), "repo-policy.json");
        using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));

        var exactIdentityRules = policy.RootElement
            .GetProperty("content_rules")
            .EnumerateArray()
            .Where(element => string.Equals(
                element.GetProperty("id").GetString(),
                "no-legacy-or-environment-identities",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(exactIdentityRules);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
