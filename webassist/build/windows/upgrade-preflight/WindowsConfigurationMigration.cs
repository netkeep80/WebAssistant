using System.Security.Cryptography;

namespace WebAssistant.UpgradePreflight;

internal static class WindowsConfigurationMigration
{
    internal const string ExampleFileName = "appsettings.example.json";
    internal const string LifecycleMarkerFileName = ".persistent-config-v1";

    internal static void Migrate(
        string legacyApplicationRoot,
        string commonApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyApplicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationDataRoot);

        if (!Directory.Exists(legacyApplicationRoot))
        {
            return;
        }

        var destinationRoot = Path.Combine(
            Path.GetFullPath(commonApplicationDataRoot),
            "WebAssistant",
            "config");
        Directory.CreateDirectory(destinationRoot);

        var markerPath = Path.Combine(
            destinationRoot,
            LifecycleMarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        foreach (var source in Directory
                     .EnumerateFiles(
                         Path.GetFullPath(legacyApplicationRoot),
                         "appsettings*.json",
                         SearchOption.TopDirectoryOnly)
                     .Where(path => !string.Equals(
                         Path.GetFileName(path),
                         ExampleFileName,
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(
                destinationRoot,
                Path.GetFileName(source));

            if (!File.Exists(destination))
            {
                File.Copy(source, destination, overwrite: false);
                continue;
            }

            if (!FilesEqual(source, destination))
            {
                PreserveConflict(source, destinationRoot);
            }
        }

        WriteLifecycleMarker(markerPath);
    }

    private static void WriteLifecycleMarker(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            "persistent-config-v1\n");
        File.Move(temporary, path, overwrite: true);
    }

    private static void PreserveConflict(
        string source,
        string destinationRoot)
    {
        var fileName = Path.GetFileName(source);
        var candidate = Path.Combine(
            destinationRoot,
            $"legacy-{fileName}");

        if (!File.Exists(candidate))
        {
            File.Copy(source, candidate, overwrite: false);
            return;
        }

        if (FilesEqual(source, candidate))
        {
            return;
        }

        var hash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(source)))
            .ToLowerInvariant()[..12];
        candidate = Path.Combine(
            destinationRoot,
            $"legacy-{hash}-{fileName}");

        if (!File.Exists(candidate))
        {
            File.Copy(source, candidate, overwrite: false);
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
