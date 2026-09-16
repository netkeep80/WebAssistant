using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WebAssistant.FileSystem;

internal sealed class WindowsRootedFileSystem : IRootedFileSystem, IDisposable
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint DELETE = 0x00010000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_READ_DATA = 0x0001;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint DirectoryAnchorAccess =
        FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE;

    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint ShareAll =
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    private const uint FILE_OPEN = 1;
    private const uint FILE_CREATE = 2;
    private const uint FILE_OPEN_IF = 3;
    private const uint FILE_DIRECTORY_FILE = 0x00000001;
    private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    private const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    private const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    private const uint OBJ_CASE_INSENSITIVE = 0x00000040;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint OPEN_EXISTING = 3;

    private const int FileStandardInfo = 1;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int NtFileRenameInformation = 10;
    private const int NtFileDispositionInformation = 13;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NO_MORE_FILES = 18;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_FILE_EXISTS = 80;
    private const int ERROR_DIR_NOT_EMPTY = 145;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const int ERROR_DIRECTORY = 267;
    private const int ERROR_CANT_ACCESS_FILE = 1920;

    private const string InternalPrefix = ".webassistant-";
    private const int DirectoryBufferSize = 64 * 1024;
    private const int StreamBufferSize = 64 * 1024;
    private const int FileIdBothDirectoryFileNameOffset = 104;

    private readonly SafeFileHandle rootHandle;
    private bool disposed;

    internal WindowsRootedFileSystem(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WindowsRootedFileSystem поддерживается только на Windows.");
        }

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("RootDirectory не задан.", nameof(rootDirectory));
        }

        rootHandle = CreateFile(
            Path.GetFullPath(rootDirectory.Trim()),
            DirectoryAnchorAccess,
            ShareAll,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (rootHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            rootHandle.Dispose();
            throw MapWin32Error(error, "RootDirectory недоступен.");
        }

        try
        {
            EnsureNotReparse(rootHandle, "RootDirectory не может быть reparse point.");
            EnsureDirectory(rootHandle);
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
        using var directory = OpenDirectoryPath(parsed.Segments);
        var page = FileSystemListingPolicy.CreatePage(
            parsed.Value,
            EnumerateEntries(directory, cancellationToken),
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

        using var created = OpenRelative(
            parent,
            name,
            GENERIC_READ | GENERIC_WRITE | DELETE | SYNCHRONIZE,
            FILE_CREATE,
            FILE_ATTRIBUTE_DIRECTORY,
            FILE_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось создать каталог.");
        EnsureNotReparse(created, "Созданный каталог неожиданно является reparse point.");
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

        using var parent = OpenParent(parsed, out var name);
        var file = OpenRelative(
            parent,
            name,
            FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN,
            0,
            FILE_NON_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось открыть файл.");

        try
        {
            EnsureNotReparse(file, "Чтение через reparse point запрещено.");
            EnsureSingleLink(file);
            Stream stream = new FileStream(
                file,
                FileAccess.Read,
                bufferSize: StreamBufferSize,
                isAsync: false);
            file = null!;
            return ValueTask.FromResult(stream);
        }
        finally
        {
            file?.Dispose();
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
            var stagingFile = OpenInternalRelative(
                stagingDirectory,
                stagingName,
                GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_CREATE,
                0,
                FILE_NON_DIRECTORY_FILE |
                FILE_OPEN_REPARSE_POINT |
                FILE_SYNCHRONOUS_IO_NONALERT,
                "Не удалось создать staging-файл.");
            stagingCreated = true;

            try
            {
                EnsureNotReparse(stagingFile, "Staging-файл неожиданно является reparse point.");
                EnsureSingleLink(stagingFile);
                using var stagingStream = new FileStream(
                    stagingFile,
                    FileAccess.Write,
                    bufferSize: StreamBufferSize,
                    isAsync: false);
                stagingFile = null!;

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
            finally
            {
                stagingFile?.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var readyToCommit = OpenInternalRelative(
                stagingDirectory,
                stagingName,
                DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_OPEN,
                0,
                FILE_NON_DIRECTORY_FILE |
                FILE_OPEN_REPARSE_POINT |
                FILE_SYNCHRONOUS_IO_NONALERT,
                "Не удалось открыть staging-файл для публикации.");
            EnsureNotReparse(readyToCommit, "Staging-файл изменился на reparse point.");
            EnsureSingleLink(readyToCommit);
            RenameRelativeNoReplace(
                readyToCommit,
                destinationParent,
                destinationName);
            committed = true;
        }
        finally
        {
            if (stagingCreated && !committed)
            {
                TryDeleteInternalFile(stagingDirectory, stagingName);
            }
        }
    }

    public ValueTask MoveNoReplaceAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var source = FileSystemPathPolicy.Parse(sourceRelativePath, allowRoot: false);
        var destination = FileSystemPathPolicy.Parse(destinationRelativePath, allowRoot: false);

        using var sourceParent = OpenParent(source, out var sourceName);
        using var destinationParent = OpenParent(destination, out var destinationName);
        using var sourceHandle = OpenAnyEntry(
            sourceParent,
            sourceName,
            DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            "Не удалось открыть исходный объект для move.");

        EnsureNotReparse(sourceHandle, "Перемещение reparse point запрещено.");
        var standard = ReadStandardInfo(sourceHandle);
        if (!standard.Directory)
        {
            FileSystemPathPolicy.EnsureFileTypeAllowed(sourceName);
            FileSystemPathPolicy.EnsureFileTypeAllowed(destinationName);
            EnsureSingleLink(standard);
        }

        RenameRelativeNoReplace(
            sourceHandle,
            destinationParent,
            destinationName);
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
        using var file = OpenRelative(
            parent,
            name,
            DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN,
            0,
            FILE_NON_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось открыть файл для удаления.");

        EnsureNotReparse(file, "Удаление reparse point через API запрещено.");
        EnsureSingleLink(file);
        MarkDelete(file);
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
        using var directory = OpenRelative(
            parent,
            name,
            DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_OPEN,
            0,
            FILE_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось открыть каталог для удаления.");

        EnsureNotReparse(directory, "Удаление reparse point через API запрещено.");
        MarkDelete(directory);
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
        using var staging = OpenInternalRelative(
            rootHandle,
            FileSystemInternalNames.StagingDirectory,
            DirectoryAnchorAccess,
            FILE_OPEN_IF,
            FILE_ATTRIBUTE_DIRECTORY,
            FILE_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось создать или открыть staging-каталог.");
        EnsureNotReparse(staging, "Staging-каталог не может быть reparse point.");
        EnsureDirectory(staging);
    }

    private void CleanupOrphanedStagingFiles()
    {
        using var staging = OpenStagingDirectory();
        foreach (var entry in EnumerateEntries(staging, CancellationToken.None))
        {
            if (entry.Kind == RootedEntryKind.File &&
                FileSystemInternalNames.IsOwnedStagingFileName(entry.Name))
            {
                TryDeleteInternalFile(staging, entry.Name);
            }
        }
    }

    private SafeFileHandle OpenStagingDirectory()
    {
        var staging = OpenInternalRelative(
            rootHandle,
            FileSystemInternalNames.StagingDirectory,
            DirectoryAnchorAccess,
            FILE_OPEN,
            0,
            FILE_DIRECTORY_FILE |
            FILE_OPEN_REPARSE_POINT |
            FILE_SYNCHRONOUS_IO_NONALERT,
            "Не удалось открыть staging-каталог.");
        try
        {
            EnsureNotReparse(staging, "Staging-каталог изменился на reparse point.");
            EnsureDirectory(staging);
            return staging;
        }
        catch
        {
            staging.Dispose();
            throw;
        }
    }

    private static void TryDeleteInternalFile(
        SafeFileHandle stagingDirectory,
        string stagingName)
    {
        try
        {
            using var file = OpenInternalRelative(
                stagingDirectory,
                stagingName,
                DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_OPEN,
                0,
                FILE_NON_DIRECTORY_FILE |
                FILE_OPEN_REPARSE_POINT |
                FILE_SYNCHRONOUS_IO_NONALERT,
                "Не удалось открыть staging-файл для очистки.");
            EnsureNotReparse(file, "Staging-файл изменился на reparse point.");
            EnsureSingleLink(file);
            MarkDelete(file);
        }
        catch (FileSystemOperationException error) when (
            error.Code == FileSystemErrorCodes.NotFound)
        {
        }
        catch
        {
            // Cleanup is best effort; the original publication error remains authoritative.
        }
    }

    private IEnumerable<RootedFileSystemEntry> EnumerateEntries(
        SafeFileHandle directory,
        CancellationToken cancellationToken)
    {
        var buffer = Marshal.AllocHGlobal(DirectoryBufferSize);
        try
        {
            var infoClass = FileIdBothDirectoryRestartInfo;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!GetFileInformationByHandleEx(
                        directory,
                        infoClass,
                        buffer,
                        DirectoryBufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ERROR_NO_MORE_FILES)
                    {
                        yield break;
                    }

                    throw MapWin32Error(error, "Не удалось перечислить каталог.");
                }

                infoClass = FileIdBothDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    var current = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(current, 0);
                    var creationTime = Marshal.ReadInt64(current, 8);
                    var lastWriteTime = Marshal.ReadInt64(current, 24);
                    var endOfFile = Marshal.ReadInt64(current, 40);
                    var attributes = unchecked((uint)Marshal.ReadInt32(current, 56));
                    var nameLength = Marshal.ReadInt32(current, 60);
                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(current, FileIdBothDirectoryFileNameOffset),
                        nameLength / 2) ?? string.Empty;

                    if (name is not "." and not ".." &&
                        !name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return CreateListingEntry(
                            directory,
                            name,
                            attributes,
                            endOfFile,
                            creationTime,
                            lastWriteTime);
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    offset += nextOffset;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private RootedFileSystemEntry CreateListingEntry(
        SafeFileHandle directory,
        string name,
        uint attributes,
        long size,
        long creationTime,
        long lastWriteTime)
    {
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return new RootedFileSystemEntry(
                name,
                RootedEntryKind.Link,
                null,
                FromFileTime(creationTime),
                FromFileTime(lastWriteTime),
                FileSystemErrorCodes.UnsafeLink);
        }

        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            return new RootedFileSystemEntry(
                name,
                RootedEntryKind.Directory,
                null,
                FromFileTime(creationTime),
                FromFileTime(lastWriteTime),
                null);
        }

        string? restriction = FileSystemPathPolicy.GetRestrictionCode(name);
        try
        {
            using var file = OpenRelative(
                directory,
                name,
                FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_OPEN,
                0,
                FILE_NON_DIRECTORY_FILE |
                FILE_OPEN_REPARSE_POINT |
                FILE_SYNCHRONOUS_IO_NONALERT,
                "Не удалось проверить файл при listing.");
            EnsureNotReparse(file, "Объект изменился на reparse point.");
            if (ReadStandardInfo(file).NumberOfLinks > 1)
            {
                restriction = FileSystemErrorCodes.HardlinkRejected;
            }
        }
        catch (FileSystemOperationException error) when (
            error.Code == FileSystemErrorCodes.NotFound)
        {
            restriction = FileSystemErrorCodes.NotFound;
        }
        catch (FileSystemOperationException error) when (
            error.Code == FileSystemErrorCodes.Locked)
        {
            restriction = FileSystemErrorCodes.Locked;
        }

        return new RootedFileSystemEntry(
            name,
            RootedEntryKind.File,
            size,
            FromFileTime(creationTime),
            FromFileTime(lastWriteTime),
            restriction);
    }

    private SafeFileHandle OpenParent(
        RootedRelativePath path,
        out string name)
    {
        name = path.Segments[^1];
        return OpenDirectoryPath(path.Segments.Take(path.Segments.Count - 1));
    }

    private SafeFileHandle OpenDirectoryPath(IEnumerable<string> segments)
    {
        SafeFileHandle current = Borrow(rootHandle);
        try
        {
            foreach (var segment in segments)
            {
                var next = OpenRelative(
                    current,
                    segment,
                    DirectoryAnchorAccess,
                    FILE_OPEN,
                    0,
                    FILE_DIRECTORY_FILE |
                    FILE_OPEN_REPARSE_POINT |
                    FILE_SYNCHRONOUS_IO_NONALERT,
                    "Не удалось открыть родительский каталог.");
                try
                {
                    EnsureNotReparse(next, "Путь проходит через reparse point.");
                    EnsureDirectory(next);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAnyEntry(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        string message) =>
        OpenRelative(
            parent,
            name,
            desiredAccess,
            FILE_OPEN,
            0,
            FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT,
            message);

    private static SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint disposition,
        uint fileAttributes,
        uint createOptions,
        string message)
    {
        FileSystemPathPolicy.ValidateEntryName(name);
        return OpenRelativeCore(
            parent,
            name,
            desiredAccess,
            disposition,
            fileAttributes,
            createOptions,
            message);
    }

    private static SafeFileHandle OpenInternalRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint disposition,
        uint fileAttributes,
        uint createOptions,
        string message)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Недопустимое внутреннее имя filesystem staging.");
        }

        return OpenRelativeCore(
            parent,
            name,
            desiredAccess,
            disposition,
            fileAttributes,
            createOptions,
            message);
    }

    private static SafeFileHandle OpenRelativeCore(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint disposition,
        uint fileAttributes,
        uint createOptions,
        string message)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicode, unicodeStringPointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = OBJ_CASE_INSENSITIVE,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };

            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes,
                ShareAll,
                disposition,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw MapNtStatus(status, message);
            }

            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RenameRelativeNoReplace(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationName)
    {
        FileSystemPathPolicy.ValidateEntryName(destinationName);
        var nameBytes = Encoding.Unicode.GetBytes(destinationName);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var bytes = new byte[nameOffset + nameBytes.Length];
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(
                IntPtr.Add(buffer, rootOffset),
                destinationParent.DangerousGetHandle());
            Marshal.WriteInt32(
                IntPtr.Add(buffer, lengthOffset),
                nameBytes.Length);
            Marshal.Copy(
                nameBytes,
                0,
                IntPtr.Add(buffer, nameOffset),
                nameBytes.Length);

            var status = NtSetInformationFile(
                source,
                out _,
                buffer,
                checked((uint)bytes.Length),
                NtFileRenameInformation);
            if (status < 0)
            {
                throw MapNtStatus(status, "Не удалось переместить объект.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 0, 1);
            var status = NtSetInformationFile(
                handle,
                out _,
                buffer,
                1,
                NtFileDispositionInformation);
            if (status < 0)
            {
                throw MapNtStatus(status, "Не удалось удалить объект.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileStandardInformation ReadStandardInfo(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<FileStandardInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileStandardInfo,
                    buffer,
                    size))
            {
                throw MapWin32Error(
                    Marshal.GetLastPInvokeError(),
                    "Не удалось прочитать сведения об объекте.");
            }

            return Marshal.PtrToStructure<FileStandardInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileAttributeTagInformation ReadAttributeTagInfo(
        SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<FileAttributeTagInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfo,
                    buffer,
                    size))
            {
                throw MapWin32Error(
                    Marshal.GetLastPInvokeError(),
                    "Не удалось проверить attributes объекта.");
            }

            return Marshal.PtrToStructure<FileAttributeTagInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void EnsureNotReparse(
        SafeFileHandle handle,
        string message)
    {
        if ((ReadAttributeTagInfo(handle).FileAttributes &
             FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.UnsafeLink,
                message);
        }
    }

    private static void EnsureDirectory(SafeFileHandle handle)
    {
        if (!ReadStandardInfo(handle).Directory)
        {
            throw InvalidPath("Ожидался каталог.");
        }
    }

    private static void EnsureSingleLink(SafeFileHandle handle) =>
        EnsureSingleLink(ReadStandardInfo(handle));

    private static void EnsureSingleLink(FileStandardInformation info)
    {
        if (info.NumberOfLinks > 1)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.HardlinkRejected,
                "Операция над файлом с несколькими hard-link alias запрещена.");
        }
    }

    private static DateTimeOffset FromFileTime(long fileTime)
    {
        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static FileSystemOperationException MapNtStatus(
        int status,
        string message)
    {
        var win32 = unchecked((int)RtlNtStatusToDosError(status));
        return MapWin32Error(win32, message);
    }

    private static FileSystemOperationException MapWin32Error(
        int error,
        string message) =>
        error switch
        {
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_DIRECTORY =>
                new FileSystemOperationException(
                    FileSystemErrorCodes.NotFound,
                    message),
            ERROR_FILE_EXISTS or ERROR_ALREADY_EXISTS =>
                new FileSystemOperationException(
                    FileSystemErrorCodes.DestinationExists,
                    message),
            ERROR_DIR_NOT_EMPTY =>
                new FileSystemOperationException(
                    FileSystemErrorCodes.DirectoryNotEmpty,
                    message),
            ERROR_CANT_ACCESS_FILE =>
                new FileSystemOperationException(
                    FileSystemErrorCodes.UnsafeLink,
                    message),
            ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION =>
                new FileSystemOperationException(
                    FileSystemErrorCodes.Locked,
                    message),
            _ => new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemUnavailable,
                $"{message} win32={error}.")
        };

    private static FileSystemOperationException InvalidPath(string message) =>
        new(FileSystemErrorCodes.InvalidPath, message);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static SafeFileHandle Borrow(SafeFileHandle handle) =>
        new(handle.DangerousGetHandle(), ownsHandle: false);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
