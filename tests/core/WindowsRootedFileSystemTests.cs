using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsRootedFileSystemTests : IDisposable
{
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private readonly string testRoot;
    private readonly string root;
    private readonly string outside;

    public WindowsRootedFileSystemTests()
    {
        testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-windows-rooted-tests",
            Guid.NewGuid().ToString("N"));
        root = Path.Combine(testRoot, "root");
        outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
    }

    [Fact]
    public void WindowsRootedFileSystem_RootHandleAllowsThirdPartyDirectoryEnumerationWithoutDeleteShare()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fileSystem = new WindowsRootedFileSystem(root);
        var opened = OpenExternalDirectoryWithoutDeleteShare(root);
        using var external = opened.Handle;

        Assert.False(
            external.IsInvalid,
            $"Сторонний процесс не смог открыть RootDirectory; win32={opened.Error}.");
    }

    [Fact]
    public async Task WindowsRootedFileSystem_UploadWorksWhenThirdPartyHoldsDestinationDirectoryWithoutDeleteShare()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var incoming = Path.Combine(root, "incoming");
        Directory.CreateDirectory(incoming);
        using var fileSystem = new WindowsRootedFileSystem(root);
        var opened = OpenExternalDirectoryWithoutDeleteShare(incoming);
        using var external = opened.Handle;
        Assert.False(
            external.IsInvalid,
            $"Тестовый внешний directory handle не открылся; win32={opened.Error}.");

        await using var source = new MemoryStream("payload"u8.ToArray());
        await fileSystem.PublishNewFileAsync("incoming/payload.bin", source);

        Assert.Equal(
            "payload",
            await File.ReadAllTextAsync(Path.Combine(incoming, "payload.bin")));
    }

    [Fact]
    public async Task WindowsRootedFileSystem_NormalOperationsStayInsideRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fileSystem = new WindowsRootedFileSystem(root);
        await fileSystem.CreateDirectoryAsync("incoming");
        var originalPath = Path.Combine(root, "incoming", "a.bin");
        await File.WriteAllBytesAsync(originalPath, "payload"u8.ToArray());

        var page = await fileSystem.ListAsync("incoming", 200, null);
        var entry = Assert.Single(page.Entries);
        Assert.Equal("a.bin", entry.Name);
        Assert.Equal(RootedEntryKind.File, entry.Kind);
        Assert.Equal(7, entry.Size);
        Assert.Null(entry.RestrictionCode);

        await using (var stream = await fileSystem.OpenReadAsync("incoming/a.bin"))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("payload", await reader.ReadToEndAsync());
        }

        await fileSystem.MoveNoReplaceAsync(
            "incoming/a.bin",
            "incoming/b.bin",
            RootedEntryKind.File);
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(Path.Combine(root, "incoming", "b.bin")));

        await File.WriteAllTextAsync(
            Path.Combine(root, "incoming", "occupied.bin"),
            "original");
        var conflict = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.MoveNoReplaceAsync(
                "incoming/b.bin",
                "incoming/occupied.bin",
                RootedEntryKind.File));
        Assert.Equal(FileSystemErrorCodes.DestinationExists, conflict.Code);
        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(Path.Combine(root, "incoming", "occupied.bin")));

        await fileSystem.DeleteFileAsync("incoming/b.bin");
        await fileSystem.DeleteFileAsync("incoming/occupied.bin");
        await fileSystem.DeleteEmptyDirectoryAsync("incoming");
        Assert.False(Directory.Exists(Path.Combine(root, "incoming")));
    }

    [Fact]
    public async Task WindowsRootedFileSystem_MoveFailsClosedOnExpectedKindMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(root, "file.bin"), "payload");
        using var fileSystem = new WindowsRootedFileSystem(root);

        var directoryAsFile = await Assert.ThrowsAsync<FileSystemOperationException>(
            async () => await fileSystem.MoveNoReplaceAsync(
                "folder",
                "folder-moved",
                RootedEntryKind.File));
        Assert.Equal(FileSystemErrorCodes.InvalidPath, directoryAsFile.Code);
        Assert.True(Directory.Exists(Path.Combine(root, "folder")));
        Assert.False(Directory.Exists(Path.Combine(root, "folder-moved")));

        var fileAsDirectory = await Assert.ThrowsAsync<FileSystemOperationException>(
            async () => await fileSystem.MoveNoReplaceAsync(
                "file.bin",
                "file-moved.bin",
                RootedEntryKind.Directory));
        Assert.Equal(FileSystemErrorCodes.InvalidPath, fileAsDirectory.Code);
        Assert.True(File.Exists(Path.Combine(root, "file.bin")));
        Assert.False(File.Exists(Path.Combine(root, "file-moved.bin")));
    }

    [Fact]
    public async Task WindowsRootedFileSystem_ReparseParentAndFinalObjectAreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "OUTSIDE");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "file-link.txt"), sentinel);

        using var fileSystem = new WindowsRootedFileSystem(root);

        var parentError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("escape/sentinel.txt"));
        Assert.Equal(FileSystemErrorCodes.UnsafeLink, parentError.Code);

        var finalError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("file-link.txt"));
        Assert.Equal(FileSystemErrorCodes.UnsafeLink, finalError.Code);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task WindowsRootedFileSystem_HardLinkedFileIsRejectedForReadAndDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideFile = Path.Combine(outside, "shared.bin");
        var insideAlias = Path.Combine(root, "alias.bin");
        await File.WriteAllTextAsync(outsideFile, "SHARED");
        Assert.True(CreateHardLink(insideAlias, outsideFile, IntPtr.Zero));

        using var fileSystem = new WindowsRootedFileSystem(root);

        var readError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("alias.bin"));
        Assert.Equal(FileSystemErrorCodes.HardlinkRejected, readError.Code);

        var deleteError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.DeleteFileAsync("alias.bin"));
        Assert.Equal(FileSystemErrorCodes.HardlinkRejected, deleteError.Code);
        Assert.Equal("SHARED", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task WindowsRootedFileSystem_ConcurrentParentReplacementNeverReadsOutsideSentinel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideSentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "OUTSIDE-SENTINEL");
        var slot = Path.Combine(root, "slot");
        Directory.CreateDirectory(slot);

        using var fileSystem = new WindowsRootedFileSystem(root);
        using var cancellation = new CancellationTokenSource();
        var attacker = Task.Run(() =>
            ReplaceParentRepeatedly(slot, outside, cancellation.Token));

        try
        {
            for (var iteration = 0; iteration < 300; iteration++)
            {
                try
                {
                    await using var stream = await fileSystem.OpenReadAsync("slot/sentinel.txt");
                    using var reader = new StreamReader(stream);
                    Assert.NotEqual("OUTSIDE-SENTINEL", await reader.ReadToEndAsync());
                }
                catch (FileSystemOperationException error)
                {
                    Assert.Contains(
                        error.Code,
                        new[]
                        {
                            FileSystemErrorCodes.NotFound,
                            FileSystemErrorCodes.UnsafeLink,
                            FileSystemErrorCodes.FileSystemUnavailable,
                            FileSystemErrorCodes.Locked
                        });
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            await attacker;
        }

        Assert.Equal("OUTSIDE-SENTINEL", await File.ReadAllTextAsync(outsideSentinel));
    }

    private static (SafeFileHandle Handle, int Error) OpenExternalDirectoryWithoutDeleteShare(
        string path)
    {
        var handle = CreateFile(
            path,
            FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return (handle, error);
    }

    private static void ReplaceParentRepeatedly(
        string slot,
        string outside,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DeleteSlot(slot);
                Directory.CreateSymbolicLink(slot, outside);
                DeleteSlot(slot);
                Directory.CreateDirectory(slot);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DeleteSlot(string slot)
    {
        if (!Directory.Exists(slot) && !File.Exists(slot))
        {
            return;
        }

        var info = new DirectoryInfo(slot);
        if (!string.IsNullOrEmpty(info.LinkTarget))
        {
            Directory.Delete(slot);
            return;
        }

        Directory.Delete(slot, recursive: true);
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    public void Dispose()
    {
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch
        {
        }
    }
}