using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WebAssistant.FileSystem;

internal sealed class LinuxRootedFileSystem : IRootedFileSystem, IDisposable
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;

    private const ulong RESOLVE_NO_MAGICLINKS = 0x02;
    private const ulong RESOLVE_NO_SYMLINKS = 0x04;
    private const ulong RESOLVE_BENEATH = 0x08;
    private const ulong SecureResolveFlags =
        RESOLVE_NO_MAGICLINKS |
        RESOLVE_NO_SYMLINKS |
        RESOLVE_BENEATH;

    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_REMOVEDIR = 0x200;
    private const uint RENAME_NOREPLACE = 1;

    private const uint S_IFMT = 0xF000;
    private const uint S_IFDIR = 0x4000;
    private const uint S_IFREG = 0x8000;
    private const uint S_IFLNK = 0xA000;

    private const int EPERM = 1;
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int EBUSY = 16;
    private const int EEXIST = 17;
    private const int EXDEV = 18;
    private const int ENOTDIR = 20;
    private const int EISDIR = 21;
    private const int EINVAL = 22;
    private const int ETXTBSY = 26;
    private const int ENOSYS = 38;
    private const int ENOTEMPTY = 39;
    private const int ELOOP = 40;

    private const long SYS_openat2 = 437;
    private const string InternalPrefix = ".webassistant-";

    private readonly SafeFileHandle rootHandle;
    private bool disposed;

    internal LinuxRootedFileSystem(string rootDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "LinuxRootedFileSystem поддерживается только на Linux.");
        }

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "RootDirectory не задан.",
                nameof(rootDirectory));
        }

        var fullRoot = Path.GetFullPath(rootDirectory.Trim());
        var descriptor = Open(
            fullRoot,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw CreateRootOpenException(errno);
        }

        rootHandle = Own(descriptor);
    }

    public ValueTask<RootedFileSystemPage> ListAsync(
        string relativePath,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (limit is < 1 or > 1000)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.InvalidPath,
                "Параметр limit должен быть от 1 до 1000.");
        }

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: true);
        using var directory = OpenDirectory(parsed);
        var directoryFd = GetFd(directory);
        var directoryPath = $"/proc/self/fd/{directoryFd}";
        var afterName = DecodeCursor(cursor);

        string[] names;
        try
        {
            names = Directory
                .EnumerateFileSystemEntries(directoryPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Where(name => !name.StartsWith(
                    InternalPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.NotFound,
                "Каталог больше не существует.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                "Нет доступа к каталогу.",
                exception);
        }
        catch (IOException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                "Не удалось перечислить содержимое каталога.",
                exception);
        }

        if (afterName is not null)
        {
            names = names
                .Where(name => string.CompareOrdinal(name, afterName) > 0)
                .ToArray();
        }

        var entries = new List<RootedFileSystemEntry>(Math.Min(limit, names.Length));
        var hasMore = false;

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Count == limit)
            {
                hasMore = true;
                break;
            }

            if (!TryReadEntry(directoryFd, name, out var entry))
            {
                continue;
            }

            entries.Add(entry);
        }

        var nextCursor = hasMore && entries.Count > 0
            ? EncodeCursor(entries[^1].Name)
            : null;

        return ValueTask.FromResult(
            new RootedFileSystemPage(entries, nextCursor));
    }

    public ValueTask CreateDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
        using var parent = OpenParent(parsed, out var name);

        if (MkdirAt(GetFd(parent), name, 0x1F8) != 0)
        {
            throw MapMutationError(
                Marshal.GetLastPInvokeError(),
                "Не удалось создать каталог.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
        FileSystemPathPolicy.EnsureFileTypeAllowed(parsed.Segments[^1]);

        var descriptor = OpenAt2(
            GetFd(rootHandle),
            parsed.Value,
            O_RDONLY | O_CLOEXEC | O_NOFOLLOW,
            SecureResolveFlags);

        if (descriptor < 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось открыть файл.");
        }

        var handle = Own(descriptor);
        try
        {
            var stat = ReadStat(handle);
            EnsureRegularFile(stat);
            EnsureSingleLink(stat);

            Stream stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: true);
            handle = null!;
            return ValueTask.FromResult(stream);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public ValueTask PublishNewFileAsync(
        string relativePath,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException(
            new NotSupportedException(
                "Atomic publication будет добавлена отдельным TDD-срезом #196."));
    }

    public ValueTask MoveNoReplaceAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var source = FileSystemPathPolicy.Parse(
            sourceRelativePath,
            allowRoot: false);
        var destination = FileSystemPathPolicy.Parse(
            destinationRelativePath,
            allowRoot: false);

        using var sourceParent = OpenParent(source, out var sourceName);
        using var destinationParent = OpenParent(destination, out var destinationName);

        var sourceStat = ReadEntryStat(GetFd(sourceParent), sourceName);
        var sourceKind = sourceStat.StMode & S_IFMT;
        if (sourceKind == S_IFLNK)
        {
            throw UnsafeLink("Перемещение ссылок через API запрещено.");
        }

        if (sourceKind == S_IFREG)
        {
            FileSystemPathPolicy.EnsureFileTypeAllowed(sourceName);
            FileSystemPathPolicy.EnsureFileTypeAllowed(destinationName);
            EnsureSingleLink(sourceStat);
        }
        else if (sourceKind != S_IFDIR)
        {
            throw InvalidPath("Поддерживается перемещение только файла или каталога.");
        }

        if (RenameAt2(
                GetFd(sourceParent),
                sourceName,
                GetFd(destinationParent),
                destinationName,
                RENAME_NOREPLACE) != 0)
        {
            throw MapRenameError(
                Marshal.GetLastPInvokeError(),
                "Не удалось переместить объект.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
        using var parent = OpenParent(parsed, out var name);
        var stat = ReadEntryStat(GetFd(parent), name);
        var kind = stat.StMode & S_IFMT;

        if (kind == S_IFLNK)
        {
            throw UnsafeLink("Удаление ссылок через API запрещено.");
        }

        if (kind != S_IFREG)
        {
            throw InvalidPath("Операция удаления файла применима только к обычному файлу.");
        }

        EnsureSingleLink(stat);

        if (UnlinkAt(GetFd(parent), name, 0) != 0)
        {
            throw MapMutationError(
                Marshal.GetLastPInvokeError(),
                "Не удалось удалить файл.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteEmptyDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
        using var parent = OpenParent(parsed, out var name);
        var stat = ReadEntryStat(GetFd(parent), name);
        var kind = stat.StMode & S_IFMT;

        if (kind == S_IFLNK)
        {
            throw UnsafeLink("Удаление ссылок через API запрещено.");
        }

        if (kind != S_IFDIR)
        {
            throw InvalidPath("Операция удаления каталога применима только к каталогу.");
        }

        if (UnlinkAt(GetFd(parent), name, AT_REMOVEDIR) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ENOTEMPTY)
            {
                throw new FileSystemOperationException(
                    FileSystemErrorCodes.DirectoryNotEmpty,
                    "Каталог не пуст.");
            }

            throw MapMutationError(
                errno,
                "Не удалось удалить каталог.");
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        rootHandle.Dispose();
    }

    private SafeFileHandle OpenDirectory(RootedRelativePath path)
    {
        if (path.Segments.Count == 0)
        {
            return DuplicateRoot();
        }

        var descriptor = OpenAt2(
            GetFd(rootHandle),
            path.Value,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW,
            SecureResolveFlags);

        if (descriptor < 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось открыть каталог.");
        }

        return Own(descriptor);
    }

    private SafeFileHandle OpenParent(
        RootedRelativePath path,
        out string name)
    {
        name = path.Segments[^1];
        if (path.Segments.Count == 1)
        {
            return DuplicateRoot();
        }

        var parentPath = string.Join('/', path.Segments.Take(path.Segments.Count - 1));
        var descriptor = OpenAt2(
            GetFd(rootHandle),
            parentPath,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW,
            SecureResolveFlags);

        if (descriptor < 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось открыть родительский каталог.");
        }

        return Own(descriptor);
    }

    private SafeFileHandle DuplicateRoot()
    {
        var descriptor = Dup(GetFd(rootHandle));
        if (descriptor < 0)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                "Не удалось получить дескриптор RootDirectory.");
        }

        return Own(descriptor);
    }

    private static int OpenAt2(
        int directoryFd,
        string relativePath,
        int flags,
        ulong resolve)
    {
        var how = new OpenHow
        {
            Flags = unchecked((ulong)flags),
            Mode = 0,
            Resolve = resolve
        };

        var result = SyscallOpenAt2(
            SYS_openat2,
            directoryFd,
            relativePath,
            ref how,
            (nuint)Marshal.SizeOf<OpenHow>());

        return result < 0
            ? -1
            : checked((int)result);
    }

    private static bool TryReadEntry(
        int directoryFd,
        string name,
        out RootedFileSystemEntry entry)
    {
        if (FStatAt(
                directoryFd,
                name,
                out var stat,
                AT_SYMLINK_NOFOLLOW) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is ENOENT or ENOTDIR)
            {
                entry = null!;
                return false;
            }

            throw MapOpenError(errno, "Не удалось прочитать метаданные объекта.");
        }

        var type = stat.StMode & S_IFMT;
        RootedEntryKind kind;
        long? size;
        string? restriction;

        if (type == S_IFLNK)
        {
            kind = RootedEntryKind.Link;
            size = null;
            restriction = FileSystemErrorCodes.UnsafeLink;
        }
        else if (type == S_IFDIR)
        {
            kind = RootedEntryKind.Directory;
            size = null;
            restriction = null;
        }
        else if (type == S_IFREG)
        {
            kind = RootedEntryKind.File;
            size = stat.StSize;
            restriction = stat.StNlink > 1
                ? FileSystemErrorCodes.HardlinkRejected
                : FileSystemPathPolicy.GetRestrictionCode(name);
        }
        else
        {
            kind = RootedEntryKind.Link;
            size = null;
            restriction = FileSystemErrorCodes.UnsafeLink;
        }

        entry = new RootedFileSystemEntry(
            name,
            kind,
            size,
            ToTimestamp(stat.StCtim),
            ToTimestamp(stat.StMtim),
            restriction);
        return true;
    }

    private static LinuxStat ReadEntryStat(int directoryFd, string name)
    {
        if (FStatAt(
                directoryFd,
                name,
                out var stat,
                AT_SYMLINK_NOFOLLOW) != 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось прочитать метаданные объекта.");
        }

        return stat;
    }

    private static LinuxStat ReadStat(SafeFileHandle handle)
    {
        if (FStat(GetFd(handle), out var stat) != 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось проверить открытый объект.");
        }

        return stat;
    }

    private static void EnsureRegularFile(LinuxStat stat)
    {
        if ((stat.StMode & S_IFMT) != S_IFREG)
        {
            throw InvalidPath("Операция применима только к обычному файлу.");
        }
    }

    private static void EnsureSingleLink(LinuxStat stat)
    {
        if (stat.StNlink > 1)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.HardlinkRejected,
                "Операция над файлом с несколькими hard-link alias запрещена.");
        }
    }

    private static DateTimeOffset ToTimestamp(LinuxTimespec value)
    {
        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds(value.Seconds)
                .AddTicks(value.Nanoseconds / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static string EncodeCursor(string name) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    private static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.InvalidPath,
                "Cursor каталога имеет недопустимый формат.",
                exception);
        }
    }

    private static FileSystemOperationException CreateRootOpenException(int errno)
    {
        if (errno == ELOOP)
        {
            return UnsafeLink("RootDirectory не может быть символической ссылкой.");
        }

        return new FileSystemOperationException(
            FileSystemErrorCodes.FileSystemUnavailable,
            $"RootDirectory недоступен (errno={errno}).");
    }

    private static FileSystemOperationException MapOpenError(
        int errno,
        string message)
    {
        return errno switch
        {
            ENOENT or ENOTDIR => new FileSystemOperationException(
                FileSystemErrorCodes.NotFound,
                message),
            ELOOP or EXDEV => UnsafeLink(message),
            EACCES or EPERM or EBUSY or ETXTBSY => new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                message),
            ENOSYS or EINVAL => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                "Ядро Linux не предоставляет требуемую безопасную openat2 semantics."),
            _ => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                $"{message} errno={errno}.")
        };
    }

    private static FileSystemOperationException MapMutationError(
        int errno,
        string message)
    {
        return errno switch
        {
            EEXIST => new FileSystemOperationException(
                FileSystemErrorCodes.DestinationExists,
                message),
            ENOENT or ENOTDIR => new FileSystemOperationException(
                FileSystemErrorCodes.NotFound,
                message),
            ELOOP or EXDEV => UnsafeLink(message),
            EACCES or EPERM or EBUSY or ETXTBSY => new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                message),
            _ => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                $"{message} errno={errno}.")
        };
    }

    private static FileSystemOperationException MapRenameError(
        int errno,
        string message)
    {
        return errno switch
        {
            EEXIST => new FileSystemOperationException(
                FileSystemErrorCodes.DestinationExists,
                message),
            ENOENT or ENOTDIR => new FileSystemOperationException(
                FileSystemErrorCodes.NotFound,
                message),
            ELOOP => UnsafeLink(message),
            EXDEV => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                "Move разрешён только как atomic rename внутри одного filesystem."),
            EACCES or EPERM or EBUSY or ETXTBSY => new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                message),
            _ => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                $"{message} errno={errno}.")
        };
    }

    private static FileSystemOperationException UnsafeLink(string message) =>
        new(FileSystemErrorCodes.UnsafeLink, message);

    private static FileSystemOperationException InvalidPath(string message) =>
        new(FileSystemErrorCodes.InvalidPath, message);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static SafeFileHandle Own(int descriptor) =>
        new((IntPtr)descriptor, ownsHandle: true);

    private static int GetFd(SafeFileHandle handle) =>
        checked((int)handle.DangerousGetHandle());

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong StDev;
        internal ulong StIno;
        internal ulong StNlink;
        internal uint StMode;
        internal uint StUid;
        internal uint StGid;
        internal int Pad0;
        internal ulong StRdev;
        internal long StSize;
        internal long StBlksize;
        internal long StBlocks;
        internal LinuxTimespec StAtim;
        internal LinuxTimespec StMtim;
        internal LinuxTimespec StCtim;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "dup")]
    private static extern int Dup(int oldFd);

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")]
    private static extern int MkdirAt(int directoryFd, string path, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "unlinkat")]
    private static extern int UnlinkAt(int directoryFd, string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "renameat2")]
    private static extern int RenameAt2(
        int oldDirectoryFd,
        string oldPath,
        int newDirectoryFd,
        string newPath,
        uint flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int FStat(int fd, out LinuxStat stat);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstatat")]
    private static extern int FStatAt(
        int directoryFd,
        string path,
        out LinuxStat stat,
        int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
    private static extern long SyscallOpenAt2(
        long number,
        int directoryFd,
        string path,
        ref OpenHow how,
        nuint size);
}
