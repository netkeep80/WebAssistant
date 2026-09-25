using System.Globalization;
using System.Text;

namespace WebAssistant.FileSystem;

internal static class FileSystemErrorCodes
{
    internal const string InvalidPath = "invalid_path";
    internal const string FileSystemPathInvalid = "filesystem_path_invalid";
    internal const string FileSystemNotConfigured = "filesystem_not_configured";
    internal const string FileSystemConfigurationInvalid = "filesystem_configuration_invalid";
    internal const string FileSystemRootNotFound = "filesystem_root_not_found";
    internal const string FileSystemRootUnavailable = "filesystem_root_unavailable";
    internal const string NotFound = "not_found";
    internal const string DestinationExists = "destination_exists";
    internal const string DirectoryNotEmpty = "directory_not_empty";
    internal const string UnsafeLink = "unsafe_link";
    internal const string HardlinkRejected = "hardlink_rejected";
    internal const string BlockedFileType = "blocked_file_type";
    internal const string Locked = "locked";
    internal const string AtomicMoveUnavailable = "atomic_move_unavailable";
    internal const string FileSystemUnavailable = "filesystem_unavailable";
}

internal static class FileSystemInternalNames
{
    internal const string StagingDirectory = ".webassistant-staging";
    internal const string StagingFilePrefix = "upload-";

    internal static string CreateStagingFileName() =>
        string.Concat(StagingFilePrefix, Guid.NewGuid().ToString("N"));

    internal static bool IsOwnedStagingFileName(string name) =>
        name.StartsWith(StagingFilePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[StagingFilePrefix.Length..], "N", out _);
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

    ValueTask<Stream> OpenStableReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    ValueTask PublishNewFileAsync(
        string relativePath,
        Stream source,
        CancellationToken cancellationToken = default);

    ValueTask MoveNoReplaceAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default);

    ValueTask MoveNoReplaceToAsync(
        string sourceRelativePath,
        IRootedFileSystem destinationFileSystem,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default);

    ValueTask MoveReplaceToAsync(
        string sourceRelativePath,
        IRootedFileSystem destinationFileSystem,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default);

    ValueTask DeleteFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    ValueTask DeleteEmptyDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default);
}

internal static class FileSystemListingPolicy
{
    internal static RootedFileSystemPage CreatePage(
        string relativePath,
        IEnumerable<RootedFileSystemEntry> entries,
        int limit,
        string? cursor)
    {
        if (limit is < 1 or > 1000)
        {
            throw InvalidPath("Параметр limit должен быть от 1 до 1000.");
        }

        var offset = DecodeOffset(relativePath, cursor);
        using var enumerator = entries.GetEnumerator();

        for (var skipped = 0; skipped < offset; skipped++)
        {
            if (!enumerator.MoveNext())
            {
                return new RootedFileSystemPage(
                    Array.Empty<RootedFileSystemEntry>(),
                    null);
            }
        }

        var pageEntries = new List<RootedFileSystemEntry>(limit);
        while (pageEntries.Count < limit && enumerator.MoveNext())
        {
            pageEntries.Add(enumerator.Current);
        }

        var hasMore = enumerator.MoveNext();
        var nextCursor = hasMore
            ? EncodeCursor(relativePath, checked(offset + pageEntries.Count))
            : null;

        return new RootedFileSystemPage(pageEntries, nextCursor);
    }

    private static string EncodeCursor(string relativePath, int offset)
    {
        var payload = string.Concat(
            relativePath,
            "\0",
            offset.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static int DecodeOffset(string relativePath, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException exception)
        {
            throw InvalidPath("Cursor каталога имеет недопустимый формат.", exception);
        }

        var separator = payload.IndexOf('\0');
        if (separator < 0 ||
            !string.Equals(
                payload[..separator],
                relativePath,
                StringComparison.Ordinal) ||
            !int.TryParse(
                payload[(separator + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset) ||
            offset < 0)
        {
            throw InvalidPath("Cursor не принадлежит запрошенному каталогу.");
        }

        return offset;
    }

    private static FileSystemOperationException InvalidPath(
        string message,
        Exception? innerException = null) =>
        new(FileSystemErrorCodes.InvalidPath, message, innerException);
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
