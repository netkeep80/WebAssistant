namespace WebAssistant.FileSystem;

internal sealed class RootedFileSystemProvider : IDisposable
{
    private readonly string rootDirectory;
    private readonly object sync = new();
    private IRootedFileSystem? fileSystem;
    private bool disposed;

    internal RootedFileSystemProvider(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
        _ = TryGetFileSystem(out _);
    }

    internal bool IsAvailable => TryGetFileSystem(out _);

    internal IRootedFileSystem? FileSystem =>
        TryGetFileSystem(out var current) ? current : null;

    internal bool TryGetFileSystem(out IRootedFileSystem? current)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (fileSystem is not null)
            {
                current = fileSystem;
                return true;
            }

            try
            {
                fileSystem = Create(rootDirectory);
                current = fileSystem;
                return true;
            }
            catch (Exception exception) when (IsExpectedCapabilityFailure(exception))
            {
                current = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            (fileSystem as IDisposable)?.Dispose();
            fileSystem = null;
        }
    }

    private static IRootedFileSystem Create(string rootDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRootedFileSystem(rootDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxRootedFileSystem(rootDirectory);
        }

        throw new PlatformNotSupportedException(
            "Filesystem capability поддерживается только на Windows и Linux.");
    }

    private static bool IsExpectedCapabilityFailure(Exception exception) =>
        exception is FileSystemOperationException or
        ArgumentException or
        IOException or
        UnauthorizedAccessException or
        PlatformNotSupportedException;
}
