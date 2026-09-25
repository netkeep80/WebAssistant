using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WebAssistant.FileSystem;

internal sealed class LinuxRootedFileSystem : IRootedFileSystem, IDisposable
{
    private const int O_RDONLY = 0;
    private const int O_WRONLY = 1;
    private const int O_CREAT = 0x40;
    private const int O_EXCL = 0x80;
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
    private const uint STATX_BTIME = 0x800;
    private const uint RENAME_NOREPLACE = 1;

    private const int F_SETOWN = 8;
    private const int F_SETSIG = 10;
    private const int F_SETLEASE = 1024;
    private const int F_GETLEASE = 1025;
    private const int F_RDLCK = 0;
    private const int F_UNLCK = 2;
    private const int SIGURG = 23;

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
    private const int StreamBufferSize = 64 * 1024;
    private static readonly int DirentNameOffset = checked((int)Marshal.OffsetOf<LinuxDirent>(nameof(LinuxDirent.Name)));

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
        try
        {
            EnsureStagingDirectory();
            CleanupOrphanedStagingFiles();
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    public ValueTask<RootedFileSystemPage> ListAsync(
        string relativePath,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: true);
        using var directory = OpenDirectory(parsed);
        var page = FileSystemListingPolicy.CreatePage(
            parsed.Value,
            EnumerateEntries(GetFd(directory), cancellationToken),
            limit,
            cursor);
        return ValueTask.FromResult(page);
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
                bufferSize: StreamBufferSize,
                isAsync: false);
            handle = null!;
            return ValueTask.FromResult(stream);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public ValueTask<Stream> OpenStableReadAsync(
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
                "Не удалось открыть файл для стабильного чтения.");
        }

        var handle = Own(descriptor);
        try
        {
            var stat = ReadStat(handle);
            EnsureRegularFile(stat);
            EnsureSingleLink(stat);
            ConfigureStableReadLease(descriptor);

            var fileStream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: StreamBufferSize,
                isAsync: false);
            handle = null!;
            return ValueTask.FromResult<Stream>(
                new LinuxStableReadStream(fileStream, descriptor));
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public async ValueTask PublishNewFileAsync(
        string relativePath,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
        FileSystemPathPolicy.EnsureFileTypeAllowed(destination.Segments[^1]);

        using var destinationParent = OpenParent(destination, out var destinationName);
        using var stagingDirectory = OpenStagingDirectory();
        var stagingName = FileSystemInternalNames.CreateStagingFileName();
        var stagingCreated = false;
        var committed = false;

        try
        {
            var descriptor = OpenAt2(
                GetFd(stagingDirectory),
                stagingName,
                O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                SecureResolveFlags,
                mode: 0x180);
            if (descriptor < 0)
            {
                throw MapMutationError(
                    Marshal.GetLastPInvokeError(),
                    "Не удалось создать staging-файл.");
            }

            stagingCreated = true;
            using (var stagingStream = new FileStream(
                Own(descriptor),
                FileAccess.Write,
                bufferSize: StreamBufferSize,
                isAsync: false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(
                            buffer.AsMemory(0, StreamBufferSize),
                            cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        stagingStream.Write(buffer, 0, read);
                    }

                    stagingStream.Flush();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (RenameAt2(
                    GetFd(stagingDirectory),
                    stagingName,
                    GetFd(destinationParent),
                    destinationName,
                    RENAME_NOREPLACE) != 0)
            {
                throw MapRenameError(
                    Marshal.GetLastPInvokeError(),
                    "Не удалось опубликовать staging-файл.");
            }

            committed = true;
        }
        finally
        {
            if (stagingCreated && !committed)
            {
                _ = UnlinkAt(GetFd(stagingDirectory), stagingName, 0);
            }
        }
    }

    public ValueTask MoveNoReplaceAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default) =>
        MoveNoReplaceToAsync(
            sourceRelativePath,
            this,
            destinationRelativePath,
            expectedKind,
            cancellationToken);

    public ValueTask MoveNoReplaceToAsync(
        string sourceRelativePath,
        IRootedFileSystem destinationFileSystem,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(
            sourceRelativePath,
            destinationFileSystem,
            destinationRelativePath,
            expectedKind,
            replaceExisting: false,
            cancellationToken);

    public ValueTask MoveReplaceToAsync(
        string sourceRelativePath,
        IRootedFileSystem destinationFileSystem,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(
            sourceRelativePath,
            destinationFileSystem,
            destinationRelativePath,
            expectedKind,
            replaceExisting: true,
            cancellationToken);

    private ValueTask MoveToAsync(
        string sourceRelativePath,
        IRootedFileSystem destinationFileSystem,
        string destinationRelativePath,
        RootedEntryKind expectedKind,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (destinationFileSystem is not LinuxRootedFileSystem destination)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.AtomicMoveUnavailable,
                "Atomic move между разными native filesystem implementations недоступен.");
        }

        if (replaceExisting && expectedKind != RootedEntryKind.File)
        {
            throw InvalidPath("Atomic replace разрешён только для обычных файлов.");
        }

        destination.ThrowIfDisposed();
        var source = FileSystemPathPolicy.Parse(
            sourceRelativePath,
            allowRoot: false);
        var destinationPath = FileSystemPathPolicy.Parse(
            destinationRelativePath,
            allowRoot: false);

        using var sourceParent = OpenParent(source, out var sourceName);
        using var destinationParent = destination.OpenParent(
            destinationPath,
            out var destinationName);

        var sourceStat = ReadEntryStat(GetFd(sourceParent), sourceName);
        var sourceKind = sourceStat.StMode & S_IFMT;
        if (sourceKind == S_IFLNK)
        {
            throw UnsafeLink("Перемещение ссылок через API запрещено.");
        }

        if (expectedKind == RootedEntryKind.File)
        {
            if (sourceKind != S_IFREG)
            {
                throw InvalidPath(
                    "Тип исходного объекта изменился до atomic move: ожидался файл.");
            }

            FileSystemPathPolicy.EnsureFileTypeAllowed(sourceName);
            FileSystemPathPolicy.EnsureFileTypeAllowed(destinationName);
            EnsureSingleLink(sourceStat);
            if (replaceExisting)
            {
                EnsureReplaceableDestinationFile(
                    GetFd(destinationParent),
                    destinationName);
            }
        }
        else if (expectedKind == RootedEntryKind.Directory)
        {
            if (sourceKind != S_IFDIR)
            {
                throw InvalidPath(
                    "Тип исходного объекта изменился до atomic move: ожидался каталог.");
            }
        }
        else
        {
            throw InvalidPath("Atomic move поддерживает только файл или каталог.");
        }

        if (RenameAt2(
                GetFd(sourceParent),
                sourceName,
                GetFd(destinationParent),
                destinationName,
                replaceExisting ? 0u : RENAME_NOREPLACE) != 0)
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

    private void EnsureStagingDirectory()
    {
        if (MkdirAt(
                GetFd(rootHandle),
                FileSystemInternalNames.StagingDirectory,
                0x1C0) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno != EEXIST)
            {
                throw MapMutationError(
                    errno,
                    "Не удалось создать staging-каталог.");
            }
        }

        using var staging = OpenStagingDirectory();
    }

    private void CleanupOrphanedStagingFiles()
    {
        using var staging = OpenStagingDirectory();
        var stagingFd = GetFd(staging);
        try
        {
            foreach (var name in EnumerateNames(stagingFd))
            {
                if (!FileSystemInternalNames.IsOwnedStagingFileName(name) ||
                    FStatAt(stagingFd, name, out var stat, AT_SYMLINK_NOFOLLOW) != 0 ||
                    (stat.StMode & S_IFMT) != S_IFREG ||
                    stat.StNlink != 1)
                {
                    continue;
                }

                _ = UnlinkAt(stagingFd, name, 0);
            }
        }
        catch (FileSystemOperationException)
        {
        }
    }

    private SafeFileHandle OpenStagingDirectory()
    {
        var descriptor = OpenAt2(
            GetFd(rootHandle),
            FileSystemInternalNames.StagingDirectory,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW,
            SecureResolveFlags);
        if (descriptor < 0)
        {
            throw MapOpenError(
                Marshal.GetLastPInvokeError(),
                "Не удалось открыть staging-каталог.");
        }

        return Own(descriptor);
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
        ulong resolve,
        ulong mode = 0)
    {
        var how = new OpenHow
        {
            Flags = unchecked((ulong)flags),
            Mode = mode,
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

    private static IEnumerable<string> EnumerateNames(int directoryFd)
    {
        var descriptor = OpenAt2(directoryFd, ".", O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW, SecureResolveFlags);
        if (descriptor < 0)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                "Не удалось открыть независимый дескриптор каталога.");
        }

        using var ownedDescriptor = Own(descriptor);
        var directory = FdOpenDir(descriptor);
        if (directory == IntPtr.Zero)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                $"Не удалось открыть поток каталога (errno={Marshal.GetLastPInvokeError()}).");
        }

        ownedDescriptor.SetHandleAsInvalid();
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDir(directory);
                if (entry == IntPtr.Zero)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno != 0)
                    {
                        throw new FileSystemOperationException(
                            FileSystemErrorCodes.FileSystemUnavailable,
                            $"Не удалось перечислить каталог (errno={errno}).");
                    }

                    yield break;
                }

                var name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, DirentNameOffset));
                if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                {
                    yield return name;
                }
            }
        }
        finally
        {
            _ = CloseDir(directory);
        }
    }

    private static IEnumerable<RootedFileSystemEntry> EnumerateEntries(
        int directoryFd,
        CancellationToken cancellationToken)
    {
        foreach (var name in EnumerateNames(directoryFd))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (name.StartsWith(
                    InternalPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadEntry(directoryFd, name, out var entry))
            {
                yield return entry;
            }
        }
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
            CreationTimestamp(directoryFd, name, stat),
            ToTimestamp(stat.StMtim),
            restriction);
        return true;
    }

    private static DateTimeOffset CreationTimestamp(int directoryFd, string name, LinuxStat fallback)
    {
        if (StatX(directoryFd, name, AT_SYMLINK_NOFOLLOW, STATX_BTIME, out var extended) == 0 &&
            (extended.Mask & STATX_BTIME) != 0)
        {
            return ToTimestamp(new LinuxTimespec
            {
                Seconds = extended.Birth.Seconds,
                Nanoseconds = extended.Birth.Nanoseconds
            });
        }

        var change = fallback.StCtim;
        var modified = fallback.StMtim;
        var older = change.Seconds < modified.Seconds ||
            (change.Seconds == modified.Seconds && change.Nanoseconds <= modified.Nanoseconds)
            ? change
            : modified;
        return ToTimestamp(older);
    }

    private static void EnsureReplaceableDestinationFile(
        int destinationDirectoryFd,
        string destinationName)
    {
        if (FStatAt(
                destinationDirectoryFd,
                destinationName,
                out var stat,
                AT_SYMLINK_NOFOLLOW) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is ENOENT or ENOTDIR)
            {
                return;
            }

            throw MapOpenError(
                errno,
                "Не удалось проверить существующий целевой файл для atomic replace.");
        }

        var kind = stat.StMode & S_IFMT;
        if (kind == S_IFLNK)
        {
            throw UnsafeLink("Atomic replace поверх ссылки запрещён.");
        }

        if (kind != S_IFREG)
        {
            throw InvalidPath("Atomic replace разрешён только поверх обычного файла.");
        }

        EnsureSingleLink(stat);
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

    private static void ConfigureStableReadLease(int descriptor)
    {
        if (Fcntl(descriptor, F_SETOWN, GetPid()) == -1 ||
            Fcntl(descriptor, F_SETSIG, SIGURG) == -1 ||
            Fcntl(descriptor, F_SETLEASE, F_RDLCK) == -1)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                "Не удалось установить kernel-enforced stable-read lease.");
        }
    }

    private static void EnsureStableReadLease(int descriptor)
    {
        var lease = Fcntl(descriptor, F_GETLEASE, 0);
        if (lease != F_RDLCK)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.Locked,
                "Stable-read lease был снят или сломан во время чтения.");
        }
    }

    private sealed class LinuxStableReadStream : Stream
    {
        private readonly FileStream inner;
        private readonly int descriptor;
        private bool disposed;

        internal LinuxStableReadStream(FileStream inner, int descriptor)
        {
            this.inner = inner;
            this.descriptor = descriptor;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureLease();
            var read = inner.Read(buffer, offset, count);
            EnsureLease();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            EnsureLease();
            var read = inner.Read(buffer);
            EnsureLease();
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureLease();
            var read = await inner.ReadAsync(buffer, cancellationToken);
            EnsureLease();
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureLease();
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            EnsureLease();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                _ = Fcntl(descriptor, F_SETLEASE, F_UNLCK);
                if (disposing)
                {
                    inner.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                _ = Fcntl(descriptor, F_SETLEASE, F_UNLCK);
                await inner.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }

        private void EnsureLease()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureStableReadLease(descriptor);
        }
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
                FileSystemErrorCodes.AtomicMoveUnavailable,
                "Native filesystem не может выполнить atomic move между этими roots."),
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
    private struct LinuxDirent
    {
        internal ulong Inode;
        internal long Offset;
        internal ushort RecordLength;
        internal byte Type;
        internal byte Name;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatX
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(80)] internal LinuxStatXTimestamp Birth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatXTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
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

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int Fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "getpid")]
    private static extern int GetPid();

    [DllImport("libc", SetLastError = true, EntryPoint = "dup")]
    private static extern int Dup(int oldFd);

    [DllImport("libc", SetLastError = true, EntryPoint = "fdopendir")]
    private static extern IntPtr FdOpenDir(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "readdir")]
    private static extern IntPtr ReadDir(IntPtr directory);

    [DllImport("libc", SetLastError = true, EntryPoint = "closedir")]
    private static extern int CloseDir(IntPtr directory);

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

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int StatX(
        int directoryFd,
        string path,
        int flags,
        uint mask,
        out LinuxStatX stat);

    [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
    private static extern long SyscallOpenAt2(
        long number,
        int directoryFd,
        string path,
        ref OpenHow how,
        nuint size);
}
