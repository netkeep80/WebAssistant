using System.Runtime.InteropServices;
using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsRootedFileSystemTests : IDisposable
{
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
            "incoming/b.bin");
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(Path.Combine(root, "incoming", "b.bin")));

        await File.WriteAllTextAsync(
            Path.Combine(root, "incoming", "occupied.bin"),
            "original");
        var conflict = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.MoveNoReplaceAsync(
                "incoming/b.bin",
                "incoming/occupied.bin"));
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
