using System.Text.Json;

namespace WebAssistant.CoreTests;

internal static class ConformanceGraphValidator
{
    public static IReadOnlyList<string> ValidateCurrentRepository(string repositoryRoot) => [];

    public static IReadOnlyList<string> Validate(
        JsonElement contract,
        JsonElement conformance,
        Func<string, bool> pathExists,
        Func<string, string?> readText) => [];
}
