namespace WebAssistant.FileSystem;

internal static class FileSystemErrorCodes
{
    internal const string InvalidPath = "invalid_path";
    internal const string NotFound = "not_found";
    internal const string DestinationExists = "destination_exists";
    internal const string DirectoryNotEmpty = "directory_not_empty";
    internal const string UnsafeLink = "unsafe_link";
    internal const string HardlinkRejected = "hardlink_rejected";
    internal const string BlockedFileType = "blocked_file_type";
    internal const string Locked = "locked";
    internal const string FileSystemUnavailable = "filesystem_unavailable";
}

internal sealed class FileSystemOperationException : Exception
{
    internal FileSystemOperationException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed record RootedRelativePath(
    string Value,
    IReadOnlyList<string> Segments);

internal enum RootedEntryKind
{
    File,
    Directory,
    Link
}

internal sealed record RootedFileSystemEntry(
    string Name,
    RootedEntryKind Kind,
    long? Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    string? RestrictionCode);

internal sealed record RootedFileSystemPage(
    IReadOnlyList<RootedFileSystemEntry> Entries,
    string? NextCursor);

internal interface IRootedFileSystem
{
    ValueTask<RootedFileSystemPage> ListAsync(
        string relativePath,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    ValueTask CreateDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    ValueTask PublishNewFileAsync(
        string relativePath,
        Stream source,
        CancellationToken cancellationToken = default);

    ValueTask MoveNoReplaceAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default);

    ValueTask DeleteFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    ValueTask DeleteEmptyDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default);
}

internal static class FileSystemPathPolicy
{
    private const string ReservedInternalPrefix = ".webassistant-";

    private static readonly HashSet<string> BlockedExtensions = new(
        new[]
        {
            ".exe", ".com", ".bat", ".cmd",
            ".ps1", ".psm1",
            ".vbs", ".vbe",
            ".js", ".jse",
            ".wsf", ".wsh", ".hta",
            ".msi", ".msp",
            ".scr", ".cpl",
            ".sh", ".bash", ".zsh", ".fish",
            ".desktop",
            ".html", ".htm",
            ".svg"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);

    internal static RootedRelativePath Parse(
        string? relativePath,
        bool allowRoot)
    {
        if (relativePath is null)
        {
            throw InvalidPath("Относительный путь не задан.");
        }

        if (relativePath.Length == 0)
        {
            if (allowRoot)
            {
                return new RootedRelativePath(
                    string.Empty,
                    Array.Empty<string>());
            }

            throw InvalidPath("Для этой операции требуется путь к объекту внутри RootDirectory.");
        }

        if (LooksAbsoluteOnSupportedPlatform(relativePath))
        {
            throw InvalidPath("Разрешены только root-relative пути внутри RootDirectory.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0)
        {
            throw InvalidPath("Относительный путь не содержит допустимых сегментов.");
        }

        foreach (var segment in segments)
        {
            ValidateEntryName(segment);
        }

        return new RootedRelativePath(
            string.Join('/', segments),
            segments);
    }

    internal static void ValidateEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.Contains("/", StringComparison.Ordinal) ||
            name.Contains("\0", StringComparison.Ordinal))
        {
            throw InvalidPath("Имя файлового объекта недопустимо.");
        }

        if (name.StartsWith(
                ReservedInternalPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath("Имя зарезервировано для внутренних операций WebAssistant.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (name.Contains("\\", StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(".", StringComparison.Ordinal) ||
            name.EndsWith(" ", StringComparison.Ordinal))
        {
            throw InvalidPath("Имя файлового объекта недопустимо в Windows.");
        }

        var deviceName = name.Split('.', 2)[0];
        if (WindowsReservedDeviceNames.Contains(deviceName))
        {
            throw InvalidPath("Имя зарезервировано Windows для системного устройства.");
        }
    }

    internal static void EnsureFileTypeAllowed(string name)
    {
        ValidateEntryName(name);

        if (IsBlockedExtension(name))
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.BlockedFileType,
                "Этот тип standalone active file запрещён политикой WebAssistant.");
        }
    }

    internal static string? GetRestrictionCode(string name) =>
        IsBlockedExtension(name)
            ? FileSystemErrorCodes.BlockedFileType
            : null;

    private static bool IsBlockedExtension(string name) =>
        BlockedExtensions.Contains(Path.GetExtension(name));

    private static bool LooksAbsoluteOnSupportedPlatform(string path)
    {
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (path.Length >= 2 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':')
        {
            return true;
        }

        return OperatingSystem.IsWindows() &&
            path.StartsWith("\\", StringComparison.Ordinal);
    }

    private static FileSystemOperationException InvalidPath(string message) =>
        new(FileSystemErrorCodes.InvalidPath, message);
}
