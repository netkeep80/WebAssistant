using System.Security.Cryptography;

namespace WebAssistant.Runtime;

internal sealed record WindowsConfigurationState(
    string ConfigurationRoot,
    string ActiveConfigurationPath,
    string LegacyConfigurationPath);

internal static class WindowsConfigurationLifecycle
{
    internal const string ExampleFileName = "appsettings.example.json";
    internal const string ActiveFileName = "appsettings.json";

    internal static WindowsConfigurationState Prepare(
        string applicationRoot,
        string commonApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationDataRoot);

        var fullApplicationRoot = Path.GetFullPath(applicationRoot);
        var configurationRoot = Path.Combine(
            Path.GetFullPath(commonApplicationDataRoot),
            "WebAssistant",
            "config");
        Directory.CreateDirectory(configurationRoot);

        foreach (var legacyPath in EnumerateLegacyRuntimeConfigurationFiles(
                     fullApplicationRoot))
        {
            var destination = Path.Combine(
                configurationRoot,
                Path.GetFileName(legacyPath));

            if (!File.Exists(destination))
            {
                File.Copy(legacyPath, destination, overwrite: false);
                continue;
            }

            if (!FilesEqual(legacyPath, destination))
            {
                PreserveLegacyConflict(
                    legacyPath,
                    configurationRoot);
            }
        }

        return new WindowsConfigurationState(
            configurationRoot,
            Path.Combine(configurationRoot, ActiveFileName),
            Path.Combine(fullApplicationRoot, ActiveFileName));
    }

    internal static string ResolveCommonApplicationDataRoot()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Не удалось определить общий каталог ProgramData.");
        }

        return root;
    }

    private static IReadOnlyList<string> EnumerateLegacyRuntimeConfigurationFiles(
        string applicationRoot) =>
        Directory.Exists(applicationRoot)
            ? Directory
                .EnumerateFiles(
                    applicationRoot,
                    "appsettings*.json",
                    SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    ExampleFileName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

    private static void PreserveLegacyConflict(
        string legacyPath,
        string configurationRoot)
    {
        var fileName = Path.GetFileName(legacyPath);
        var baseName = $"legacy-{fileName}";
        var candidate = Path.Combine(
            configurationRoot,
            baseName);

        if (!File.Exists(candidate))
        {
            File.Copy(legacyPath, candidate, overwrite: false);
            return;
        }

        if (FilesEqual(legacyPath, candidate))
        {
            return;
        }

        var hash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(legacyPath)))
            .ToLowerInvariant()[..12];
        candidate = Path.Combine(
            configurationRoot,
            $"legacy-{hash}-{fileName}");

        if (!File.Exists(candidate))
        {
            File.Copy(legacyPath, candidate, overwrite: false);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return File.ReadAllBytes(left)
            .AsSpan()
            .SequenceEqual(File.ReadAllBytes(right));
    }
}
