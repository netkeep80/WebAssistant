using System.Globalization;
using System.IO.Enumeration;
using System.Text;

namespace WebAssistant.FileSystem;

internal sealed record FileSystemListResult(
    FileSystemLogicalPath Path,
    IReadOnlyList<RootedFileSystemEntry> Entries,
    string? NextCursor);

internal sealed record FileSystemZipSelection(
    FileSystemLogicalPath Path,
    IRootedFileSystem FileSystem,
    IReadOnlyList<string> FileNames);

internal sealed class FileSystemApplicationService
{
    private const int NativePageSize = 1000;
    private const int MaximumWildcardMasks = 32;
    private const int MaximumFindNames = 100;
    private const int MaximumMoveNames = 1000;
    private readonly FileSystemRootRegistry registry;

    internal FileSystemApplicationService(FileSystemRootRegistry registry)
    {
        this.registry = registry;
    }

    internal async ValueTask<FileSystemListResult> ListAsync(
        string? path,
        string? wildcard,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > NativePageSize)
        {
            throw Invalid("Параметр limit должен быть от 1 до 1000.");
        }

        var resolved = registry.Resolve(path, allowRoot: true);
        var filter = WildcardFilter.Parse(wildcard);
        var offset = DecodeCursor(resolved.Path, filter.Key, cursor);
        var entries = new List<RootedFileSystemEntry>(limit);
        var visibleIndex = 0;
        var hasMore = false;
        string? nativeCursor = null;

        do
        {
            var page = await resolved.FileSystem.ListAsync(
                resolved.Path.RelativePath,
                NativePageSize,
                nativeCursor,
                cancellationToken);

            foreach (var entry in page.Entries)
            {
                if (!filter.IncludesForListing(entry))
                {
                    continue;
                }

                if (visibleIndex++ < offset)
                {
                    continue;
                }

                if (entries.Count < limit)
                {
                    entries.Add(entry);
                    continue;
                }

                hasMore = true;
                break;
            }

            if (hasMore)
            {
                break;
            }

            nativeCursor = page.NextCursor;
        }
        while (nativeCursor is not null);

        return new FileSystemListResult(
            resolved.Path,
            entries,
            hasMore
                ? EncodeCursor(resolved.Path, filter.Key, checked(offset + entries.Count))
                : null);
    }

    internal async ValueTask<IReadOnlyList<string>> FindAsync(
        string? directoryPath,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var requested = ValidateNames(names, MaximumFindNames, ensureFileTypeAllowed: false);
        var resolved = ResolveDirectory(directoryPath);
        var entries = await FindExactEntriesAsync(
            resolved.FileSystem,
            resolved.Path.RelativePath,
            requested,
            cancellationToken);

        var result = new List<string>(requested.Count);
        foreach (var name in requested)
        {
            if (entries.TryGetValue(name, out var entry) &&
                entry.Kind == RootedEntryKind.File &&
                entry.RestrictionCode is null)
            {
                result.Add(entry.Name);
            }
        }

        return result;
    }

    internal async ValueTask<IReadOnlyList<string>> MoveFilesAsync(
        string? sourceDirectoryPath,
        string? destinationDirectoryPath,
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        var requested = ValidateNames(
            fileNames,
            MaximumMoveNames,
            ensureFileTypeAllowed: true);
        var source = ResolveDirectory(sourceDirectoryPath);
        var destinationPath = ParseDirectoryPath(destinationDirectoryPath);
        EnsureSameRoot(source.Path, destinationPath);
        if (string.Equals(
                source.Path.RelativePath,
                destinationPath.RelativePath,
                StringComparison.Ordinal))
        {
            throw Invalid("Исходный и целевой каталоги move совпадают.");
        }

        var entries = await FindExactEntriesAsync(
            source.FileSystem,
            source.Path.RelativePath,
            requested,
            cancellationToken);
        var moved = new List<string>(requested.Count);

        foreach (var requestedName in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(requestedName, out var entry))
            {
                continue;
            }

            EnsureMovableOrdinaryFile(entry);
            var sourceRelativePath = Join(source.Path.RelativePath, entry.Name);
            var destinationRelativePath = Join(destinationPath.RelativePath, entry.Name);
            try
            {
                await source.FileSystem.MoveNoReplaceAsync(
                    sourceRelativePath,
                    destinationRelativePath,
                    cancellationToken);
                moved.Add(entry.Name);
            }
            catch (FileSystemOperationException exception) when (
                exception.Code is FileSystemErrorCodes.NotFound or
                FileSystemErrorCodes.DestinationExists)
            {
                // Batch semantics: missing source и destination conflict пропускаются.
            }
        }

        return moved;
    }

    internal async ValueTask MoveDirectoryAsync(
        string? sourcePath,
        string? destinationDirectoryPath,
        CancellationToken cancellationToken)
    {
        var sourceObject = FileSystemLogicalPath.Parse(sourcePath, allowRoot: false);
        var sourceParent = ParentOf(sourceObject, out var requestedName);
        var source = registry.Resolve(sourceParent.Value, allowRoot: true);
        var destination = ParseDirectoryPath(destinationDirectoryPath);
        EnsureSameRoot(source.Path, destination);

        var sourceEntry = await FindExactEntryAsync(
            source.FileSystem,
            source.Path.RelativePath,
            requestedName,
            cancellationToken);
        if (sourceEntry is null)
        {
            throw NotFound("Исходный каталог не найден.");
        }

        if (sourceEntry.Kind == RootedEntryKind.Link)
        {
            throw Restricted(FileSystemErrorCodes.UnsafeLink, "Перемещение ссылки запрещено.");
        }

        if (sourceEntry.Kind != RootedEntryKind.Directory)
        {
            throw Invalid("Directory move применим только к каталогу.");
        }

        var actualSourceRelativePath = Join(source.Path.RelativePath, sourceEntry.Name);
        if (string.Equals(
                destination.RelativePath,
                actualSourceRelativePath,
                StringComparison.Ordinal) ||
            destination.RelativePath.StartsWith(
                string.Concat(actualSourceRelativePath, "/"),
                StringComparison.Ordinal))
        {
            throw Invalid("Каталог нельзя перемещать в себя или своего потомка.");
        }

        var destinationRelativePath = Join(destination.RelativePath, sourceEntry.Name);
        if (string.Equals(
                destinationRelativePath,
                actualSourceRelativePath,
                StringComparison.Ordinal))
        {
            throw Invalid("Целевой каталог совпадает с исходным.");
        }

        await source.FileSystem.MoveNoReplaceAsync(
            actualSourceRelativePath,
            destinationRelativePath,
            cancellationToken);
    }

    internal async ValueTask RenameAsync(
        string? path,
        string? newName,
        CancellationToken cancellationToken)
    {
        var sourceObject = FileSystemLogicalPath.Parse(path, allowRoot: false);
        FileSystemPathPolicy.ValidateEntryName(newName);
        var sourceParent = ParentOf(sourceObject, out var requestedName);
        var source = registry.Resolve(sourceParent.Value, allowRoot: true);
        var sourceEntry = await FindExactEntryAsync(
            source.FileSystem,
            source.Path.RelativePath,
            requestedName,
            cancellationToken);
        if (sourceEntry is null)
        {
            throw NotFound("Filesystem object для rename не найден.");
        }

        if (sourceEntry.Kind == RootedEntryKind.Link)
        {
            throw Restricted(FileSystemErrorCodes.UnsafeLink, "Rename ссылки запрещён.");
        }

        if (sourceEntry.RestrictionCode is not null)
        {
            throw Restricted(sourceEntry.RestrictionCode, "Restricted filesystem object нельзя переименовывать.");
        }

        if (sourceEntry.Kind == RootedEntryKind.File)
        {
            FileSystemPathPolicy.EnsureFileTypeAllowed(newName!);
        }

        await source.FileSystem.MoveNoReplaceAsync(
            Join(source.Path.RelativePath, sourceEntry.Name),
            Join(source.Path.RelativePath, newName!),
            cancellationToken);
    }

    internal async ValueTask<FileSystemZipSelection> SelectZipFilesAsync(
        string? directoryPath,
        string? wildcard,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveDirectory(directoryPath);
        var filter = WildcardFilter.Parse(wildcard);
        var fileNames = new List<string>();
        string? nativeCursor = null;

        do
        {
            var page = await resolved.FileSystem.ListAsync(
                resolved.Path.RelativePath,
                NativePageSize,
                nativeCursor,
                cancellationToken);
            foreach (var entry in page.Entries)
            {
                if (entry.Kind == RootedEntryKind.File &&
                    entry.RestrictionCode is null &&
                    filter.IncludesFile(entry.Name))
                {
                    fileNames.Add(entry.Name);
                }
            }

            nativeCursor = page.NextCursor;
        }
        while (nativeCursor is not null);

        return new FileSystemZipSelection(
            resolved.Path,
            resolved.FileSystem,
            fileNames);
    }

    private ResolvedFileSystemPath ResolveDirectory(string? value)
    {
        var logical = ParseDirectoryPath(value);
        return registry.Resolve(logical.Value, allowRoot: true);
    }

    private static FileSystemLogicalPath ParseDirectoryPath(string? value)
    {
        if (value is not null &&
            value.EndsWith("/", StringComparison.Ordinal) &&
            value.Count(character => character == '/') > 1)
        {
            if (value.EndsWith("//", StringComparison.Ordinal))
            {
                throw Invalid("Directory path содержит пустой сегмент.");
            }

            value = value[..^1];
        }

        return FileSystemLogicalPath.Parse(value, allowRoot: true);
    }

    private static void EnsureSameRoot(
        FileSystemLogicalPath source,
        FileSystemLogicalPath destination)
    {
        if (!string.Equals(
                source.RootName,
                destination.RootName,
                StringComparison.Ordinal))
        {
            throw Invalid("Mutation между логическими корнями запрещена.");
        }
    }

    private static IReadOnlyList<string> ValidateNames(
        IReadOnlyList<string> names,
        int maximum,
        bool ensureFileTypeAllowed)
    {
        if (names.Count is < 1 || names.Count > maximum)
        {
            throw Invalid($"Количество имён должно быть от 1 до {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<string>(names.Count);
        foreach (var name in names)
        {
            FileSystemPathPolicy.ValidateEntryName(name);
            if (!unique.Add(name))
            {
                throw Invalid("Список имён содержит дубликаты.");
            }

            if (ensureFileTypeAllowed)
            {
                FileSystemPathPolicy.EnsureFileTypeAllowed(name);
            }

            validated.Add(name);
        }

        return validated;
    }

    private static async ValueTask<Dictionary<string, RootedFileSystemEntry>> FindExactEntriesAsync(
        IRootedFileSystem fileSystem,
        string relativeDirectory,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var requested = names.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, RootedFileSystemEntry>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await fileSystem.ListAsync(
                relativeDirectory,
                NativePageSize,
                cursor,
                cancellationToken);
            foreach (var entry in page.Entries)
            {
                if (requested.Contains(entry.Name))
                {
                    found.TryAdd(entry.Name, entry);
                }
            }

            if (found.Count == requested.Count)
            {
                break;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return found;
    }

    private static async ValueTask<RootedFileSystemEntry?> FindExactEntryAsync(
        IRootedFileSystem fileSystem,
        string relativeDirectory,
        string name,
        CancellationToken cancellationToken)
    {
        var entries = await FindExactEntriesAsync(
            fileSystem,
            relativeDirectory,
            new[] { name },
            cancellationToken);
        return entries.GetValueOrDefault(name);
    }

    private static void EnsureMovableOrdinaryFile(RootedFileSystemEntry entry)
    {
        if (entry.Kind == RootedEntryKind.Link)
        {
            throw Restricted(FileSystemErrorCodes.UnsafeLink, "Перемещение ссылки запрещено.");
        }

        if (entry.Kind != RootedEntryKind.File)
        {
            throw Invalid("Batch move применим только к обычным файлам.");
        }

        if (entry.RestrictionCode is not null)
        {
            throw Restricted(entry.RestrictionCode, "Restricted file нельзя перемещать.");
        }
    }

    private static FileSystemLogicalPath ParentOf(
        FileSystemLogicalPath path,
        out string name)
    {
        var separator = path.RelativePath.LastIndexOf('/');
        if (separator < 0)
        {
            name = path.RelativePath;
            return new FileSystemLogicalPath(path.RootName, string.Empty);
        }

        name = path.RelativePath[(separator + 1)..];
        return new FileSystemLogicalPath(
            path.RootName,
            path.RelativePath[..separator]);
    }

    internal static string Join(string relativeDirectory, string name) =>
        relativeDirectory.Length == 0
            ? name
            : string.Concat(relativeDirectory, "/", name);

    private static string EncodeCursor(
        FileSystemLogicalPath path,
        string wildcardKey,
        int offset)
    {
        var payload = string.Join(
            '\0',
            path.RootName,
            path.RelativePath,
            wildcardKey,
            offset.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static int DecodeCursor(
        FileSystemLogicalPath path,
        string wildcardKey,
        string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = payload.Split('\0');
            if (parts.Length != 4 ||
                !string.Equals(parts[0], path.RootName, StringComparison.Ordinal) ||
                !string.Equals(parts[1], path.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(parts[2], wildcardKey, StringComparison.Ordinal) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                offset < 0)
            {
                throw Invalid("Cursor не принадлежит requested directory/wildcard.");
            }

            return offset;
        }
        catch (FormatException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemPathInvalid,
                "Cursor каталога имеет недопустимый формат.",
                exception);
        }
    }

    private static FileSystemOperationException Invalid(string message) =>
        new(FileSystemErrorCodes.FileSystemPathInvalid, message);

    private static FileSystemOperationException NotFound(string message) =>
        new(FileSystemErrorCodes.NotFound, message);

    private static FileSystemOperationException Restricted(string code, string message) =>
        new(code, message);

    private sealed record WildcardFilter(
        bool Enabled,
        string Key,
        IReadOnlyList<string> Masks)
    {
        internal static WildcardFilter Parse(string? value)
        {
            if (value is null)
            {
                return new WildcardFilter(false, "<none>", Array.Empty<string>());
            }

            if (value.Length == 0)
            {
                throw Invalid("Wildcard не содержит масок.");
            }

            var rawMasks = value.Split(',', StringSplitOptions.None);
            if (rawMasks.Length > MaximumWildcardMasks)
            {
                throw Invalid("Слишком много wildcard masks.");
            }

            var masks = new List<string>(rawMasks.Length);
            foreach (var rawMask in rawMasks)
            {
                var mask = rawMask.Trim();
                if (mask.Length == 0 ||
                    mask.Contains('/', StringComparison.Ordinal) ||
                    mask.Contains('\\', StringComparison.Ordinal) ||
                    mask.Any(char.IsControl))
                {
                    throw Invalid("Wildcard mask имеет недопустимую форму.");
                }

                if (!masks.Contains(mask, StringComparer.OrdinalIgnoreCase))
                {
                    masks.Add(mask);
                }
            }

            if (masks.Any(mask => string.Equals(mask, "*.*", StringComparison.OrdinalIgnoreCase)))
            {
                return new WildcardFilter(true, "*.*", new[] { "*.*" });
            }

            var canonical = masks
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(mask => mask.ToUpperInvariant())
                .ToArray();
            return new WildcardFilter(
                true,
                string.Join(',', canonical),
                canonical);
        }

        internal bool IncludesForListing(RootedFileSystemEntry entry) =>
            entry.Kind != RootedEntryKind.File || IncludesFile(entry.Name);

        internal bool IncludesFile(string name)
        {
            if (!Enabled)
            {
                return true;
            }

            if (Masks.Count == 1 && Masks[0] == "*.*")
            {
                return true;
            }

            return Masks.Any(mask =>
                FileSystemName.MatchesSimpleExpression(mask, name, ignoreCase: true));
        }
    }
}
