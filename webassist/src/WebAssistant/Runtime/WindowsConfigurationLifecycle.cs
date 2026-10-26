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

}
