namespace WebAssistant.FileSystem;

internal sealed class RootedFileSystemProvider : IDisposable
{
    private readonly IDisposable? disposableFileSystem;
    private bool disposed;

    internal RootedFileSystemProvider(string rootDirectory)
    {
        try
        {
            FileSystem = Create(rootDirectory);
            disposableFileSystem = FileSystem as IDisposable;
        }
        catch (Exception exception) when (IsExpectedCapabilityFailure(exception))
        {
            FileSystem = null;
        }
    }

    internal bool IsAvailable => FileSystem is not null;

    internal IRootedFileSystem? FileSystem { get; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposableFileSystem?.Dispose();
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
