using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FilePublicationTests : IDisposable
{
    private readonly string root;
    private readonly IRootedFileSystem fileSystem;

    public FilePublicationTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-publication-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        fileSystem = CreateFileSystem(root);
    }

    [Fact]
    public async Task PublishNewFileAsync_FinalNameIsInvisibleUntilSourceCompletes()
    {
        await fileSystem.CreateDirectoryAsync("incoming");
        var source = new GatedReadStream("complete-payload"u8.ToArray());

        var publication = fileSystem.PublishNewFileAsync(
            "incoming/payload.bin",
            source).AsTask();

        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Path.Combine(root, "incoming", "payload.bin")));

        source.Release();
        await publication;

        Assert.Equal(
            "complete-payload"u8.ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(root, "incoming", "payload.bin")));
    }

    [Fact]
    public async Task PublishNewFileAsync_DestinationCollisionNeverOverwrites()
    {
        await fileSystem.CreateDirectoryAsync("incoming");
        var destination = Path.Combine(root, "incoming", "payload.bin");
        await File.WriteAllTextAsync(destination, "ORIGINAL");

        await using var source = new MemoryStream("replacement"u8.ToArray());
        var error = await Assert.ThrowsAsync<FileSystemOperationException>(async () =>
            await fileSystem.PublishNewFileAsync("incoming/payload.bin", source));

        Assert.Equal(FileSystemErrorCodes.DestinationExists, error.Code);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task PublishNewFileAsync_CancellationLeavesNoFinalOrStagingFile()
    {
        await fileSystem.CreateDirectoryAsync("incoming");
        var source = new GatedReadStream("cancel-me"u8.ToArray());
        using var cancellation = new CancellationTokenSource();

        var publication = fileSystem.PublishNewFileAsync(
            "incoming/cancelled.bin",
            source,
            cancellation.Token).AsTask();

        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await publication);

        Assert.False(File.Exists(Path.Combine(root, "incoming", "cancelled.bin")));
        var stagingDirectory = Path.Combine(
            root,
            FileSystemInternalNames.StagingDirectory);
        Assert.True(Directory.Exists(stagingDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(stagingDirectory));
    }

    [Fact]
    public async Task PublishNewFileAsync_ConcurrentSameDestinationHasExactlyOneWinner()
    {
        await fileSystem.CreateDirectoryAsync("incoming");
        await using var firstSource = new MemoryStream("FIRST"u8.ToArray());
        await using var secondSource = new MemoryStream("SECOND"u8.ToArray());

        var first = CaptureAsync(() =>
            fileSystem.PublishNewFileAsync("incoming/race.bin", firstSource).AsTask());
        var second = CaptureAsync(() =>
            fileSystem.PublishNewFileAsync("incoming/race.bin", secondSource).AsTask());

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result is null);
        var loser = Assert.Single(results, result => result is not null);
        var conflict = Assert.IsType<FileSystemOperationException>(loser);
        Assert.Equal(FileSystemErrorCodes.DestinationExists, conflict.Code);

        var content = await File.ReadAllTextAsync(Path.Combine(root, "incoming", "race.bin"));
        Assert.Contains(content, new[] { "FIRST", "SECOND" });
    }

    [Fact]
    public async Task PublishNewFileAsync_ZeroByteSourceCreatesEmptyFile()
    {
        await fileSystem.CreateDirectoryAsync("incoming");
        await using var source = new MemoryStream(Array.Empty<byte>());

        await fileSystem.PublishNewFileAsync("incoming/empty.bin", source);

        var path = Path.Combine(root, "incoming", "empty.bin");
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public async Task StartupCleanup_RemovesOnlyOwnedOrphanUploadFiles()
    {
        var cleanupRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-staging-cleanup-tests",
            Guid.NewGuid().ToString("N"));
        var stagingDirectory = Path.Combine(
            cleanupRoot,
            FileSystemInternalNames.StagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        var orphan = Path.Combine(
            stagingDirectory,
            string.Concat(
                FileSystemInternalNames.StagingFilePrefix,
                Guid.NewGuid().ToString("N")));
        var foreignFile = Path.Combine(stagingDirectory, "keep.txt");
        var prefixDirectory = Path.Combine(
            stagingDirectory,
            string.Concat(
                FileSystemInternalNames.StagingFilePrefix,
                "foreign-directory"));

        await File.WriteAllTextAsync(orphan, "ORPHAN");
        await File.WriteAllTextAsync(foreignFile, "KEEP");
        Directory.CreateDirectory(prefixDirectory);

        IDisposable? cleanupFileSystem = null;
        try
        {
            cleanupFileSystem = CreateFileSystem(cleanupRoot) as IDisposable;

            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(foreignFile));
            Assert.True(Directory.Exists(prefixDirectory));
        }
        finally
        {
            cleanupFileSystem?.Dispose();
            try
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IRootedFileSystem CreateFileSystem(string rootDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRootedFileSystem(rootDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxRootedFileSystem(rootDirectory);
        }

        throw new PlatformNotSupportedException();
    }

    public void Dispose()
    {
        (fileSystem as IDisposable)?.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class GatedReadStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly TaskCompletionSource readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool gateUsed;

        internal GatedReadStream(byte[] bytes)
        {
            inner = new MemoryStream(bytes, writable: false);
        }

        internal TaskCompletionSource ReadStarted => readStarted;

        internal void Release() => release.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!gateUsed)
            {
                gateUsed = true;
                readStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
