namespace WebAssistant.FileSystem;

internal sealed class RootedPathResolver
{
    private readonly string rootDirectory;
    private readonly string rootPrefix;
    private readonly StringComparison pathComparison;

    internal RootedPathResolver(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "Корневой каталог файловой системы не задан.",
                nameof(rootDirectory));
        }

        this.rootDirectory = Path.GetFullPath(rootDirectory.Trim());
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        rootPrefix = this.rootDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        RejectLinkOrReparsePoint(this.rootDirectory);
    }

    internal string RootDirectory => rootDirectory;

    internal string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Относительный путь не задан.");
        }

        if (LooksRootedOnAnySupportedPlatform(relativePath))
        {
            throw new InvalidOperationException("Разрешены только относительные пути внутри корневого каталога.");
        }

        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.None);

        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "Путь содержит недопустимый сегмент навигации.");
        }

        var candidate = rootDirectory;
        foreach (var segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        candidate = Path.GetFullPath(candidate);
        if (!candidate.StartsWith(rootPrefix, pathComparison))
        {
            throw new InvalidOperationException(
                "Путь выходит за пределы корневого каталога.");
        }

        var current = rootDirectory;
        RejectLinkOrReparsePoint(current);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            RejectLinkOrReparsePoint(current);
        }

        return candidate;
    }

    private static bool LooksRootedOnAnySupportedPlatform(string path)
    {
        if (Path.IsPathRooted(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith('\\'))
        {
            return true;
        }

        return path.Length >= 2 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':';
    }

    private static void RejectLinkOrReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Путь проходит через ссылку или reparse point: {path}");
        }

        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        if (!string.IsNullOrEmpty(info.LinkTarget))
        {
            throw new InvalidOperationException(
                $"Путь проходит через символическую ссылку: {path}");
        }
    }
}
