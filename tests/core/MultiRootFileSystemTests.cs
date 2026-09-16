using Microsoft.Extensions.Configuration;
using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class MultiRootFileSystemTests : IDisposable
{
    private readonly string tempRoot;
    private readonly string archiveRoot;
    private readonly string nfsRoot;

    public MultiRootFileSystemTests()
    {
        tempRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-multi-root-filesystem-tests",
            Guid.NewGuid().ToString("N"));
        archiveRoot = Path.Combine(tempRoot, "archive");
        nfsRoot = Path.Combine(tempRoot, "nfs");
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(nfsRoot);
    }

    [Fact]
    public async Task Registry_DispatchesLogicalRootsToIndependentAuthorities()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAssistant:FileSystem:archive"] = archiveRoot,
                ["WebAssistant:FileSystem:nfs"] = nfsRoot
            })
            .Build();
        using var registry = FileSystemRootRegistry.Load(configuration);

        var archive = registry.Resolve("archive/a.bin", allowRoot: false);
        var nfs = registry.Resolve("nfs/b.bin", allowRoot: false);

        Assert.NotSame(archive.FileSystem, nfs.FileSystem);

        await archive.FileSystem.PublishNewFileAsync(
            archive.Path.RelativePath,
            new MemoryStream("archive"u8.ToArray()));
        await nfs.FileSystem.PublishNewFileAsync(
            nfs.Path.RelativePath,
            new MemoryStream("nfs"u8.ToArray()));

        Assert.Equal("archive", await File.ReadAllTextAsync(Path.Combine(archiveRoot, "a.bin")));
        Assert.Equal("nfs", await File.ReadAllTextAsync(Path.Combine(nfsRoot, "b.bin")));
        Assert.False(File.Exists(Path.Combine(archiveRoot, "b.bin")));
        Assert.False(File.Exists(Path.Combine(nfsRoot, "a.bin")));
    }

    [Fact]
    public void Registry_UnavailableRoot_DoesNotReplaceAvailableAuthority()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var unavailable = Path.Combine(tempRoot, "offline");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAssistant:FileSystem:archive"] = archiveRoot,
                ["WebAssistant:FileSystem:offline"] = unavailable
            })
            .Build();
        using var registry = FileSystemRootRegistry.Load(configuration);

        var available = registry.Resolve("archive/", allowRoot: true);
        var exception = Assert.Throws<FileSystemOperationException>(() =>
            registry.Resolve("offline/", allowRoot: true));

        Assert.NotNull(available.FileSystem);
        Assert.Equal(FileSystemErrorCodes.FileSystemRootUnavailable, exception.Code);
        Assert.Equal("degraded", registry.DiagnosticState);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}
