using System.Runtime.InteropServices;
using System.Text;
using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxRootedFileSystemTests : IDisposable
{
    private readonly string testRoot;
    private readonly string root;
    private readonly string outside;

    public LinuxRootedFileSystemTests()
    {
        testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-linux-rooted-tests",
            Guid.NewGuid().ToString("N"));
        root = Path.Combine(testRoot, "root");
        outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
    }

    [Fact]
    public async Task LinuxRootedFileSystem_NormalOperationsStayInsideRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fileSystem = new LinuxRootedFileSystem(root);
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
        using (var reader = new StreamReader(stream, Encoding.UTF8))
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
    public async Task LinuxRootedFileSystem_SymlinkParentAndFinalObjectAreRejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "OUTSIDE");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        File.CreateSymbolicLink(
            Path.Combine(root, "file-link.txt"),
            Path.Combine(outside, "sentinel.txt"));

        using var fileSystem = new LinuxRootedFileSystem(root);

        var parentError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("escape/sentinel.txt"));
        Assert.Equal(FileSystemErrorCodes.UnsafeLink, parentError.Code);

        var finalError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("file-link.txt"));
        Assert.Equal(FileSystemErrorCodes.UnsafeLink, finalError.Code);

        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")));
    }

    [Fact]
    public async Task LinuxRootedFileSystem_HardLinkedFileIsRejectedForReadAndDelete()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outsideFile = Path.Combine(outside, "shared.bin");
        var insideAlias = Path.Combine(root, "alias.bin");
        await File.WriteAllTextAsync(outsideFile, "SHARED");
        Assert.Equal(0, Link(outsideFile, insideAlias));

        using var fileSystem = new LinuxRootedFileSystem(root);

        var readError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.OpenReadAsync("alias.bin"));
        Assert.Equal(FileSystemErrorCodes.HardlinkRejected, readError.Code);

        var deleteError = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.DeleteFileAsync("alias.bin"));
        Assert.Equal(FileSystemErrorCodes.HardlinkRejected, deleteError.Code);
        Assert.Equal("SHARED", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task LinuxRootedFileSystem_ConcurrentParentReplacementNeverReadsOutsideSentinel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outsideSentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "OUTSIDE-SENTINEL");
        var slot = Path.Combine(root, "slot");
        Directory.CreateDirectory(slot);

        using var fileSystem = new LinuxRootedFileSystem(root);
        using var cancellation = new CancellationTokenSource();
        var attacker = Task.Run(() => ReplaceParentRepeatedly(slot, cancellation.Token));

        try
        {
            for (var iteration = 0; iteration < 300; iteration++)
            {
                try
                {
                    await using var stream = await fileSystem.OpenReadAsync("slot/sentinel.txt");
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = await reader.ReadToEndAsync();
                    Assert.NotEqual("OUTSIDE-SENTINEL", content);
                }
                catch (FileSystemOperationException error)
                {
                    Assert.Contains(
                        error.Code,
                        new[]
                        {
                            FileSystemErrorCodes.NotFound,
                            FileSystemErrorCodes.UnsafeLink,
                            FileSystemErrorCodes.FileSystemUnavailable
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
        CancellationToken cancellationToken)
    {
        var outside = Directory.GetParent(slot)!.Parent!.FullName;
        outside = Path.Combine(outside, "outside");

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

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

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
