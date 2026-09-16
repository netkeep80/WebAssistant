using Microsoft.Extensions.Configuration;

namespace WebAssistant.FileSystem;

internal enum FileSystemRegistryState
{
    NotConfigured,
    ConfigurationInvalid,
    Configured
}

internal sealed record FileSystemLogicalPath(
    string RootName,
    string RelativePath)
{
    internal string Value => RelativePath.Length == 0
        ? string.Concat(RootName, "/")
        : string.Concat(RootName, "/", RelativePath);

    internal static FileSystemLogicalPath Parse(string? value, bool allowRoot)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Contains('\\') ||
            value.Any(char.IsControl))
        {
            throw InvalidPath();
        }

        var separator = value.IndexOf('/');
        if (separator <= 0)
        {
            throw InvalidPath();
        }

        var rootName = value[..separator];
        if (!FileSystemRootRegistry.IsValidRootName(rootName))
        {
            throw InvalidPath();
        }

        var relativePath = value[(separator + 1)..];
        if (relativePath.Length == 0)
        {
            if (!allowRoot)
            {
                throw InvalidPath();
            }

            return new FileSystemLogicalPath(rootName, string.Empty);
        }

        try
        {
            var parsed = FileSystemPathPolicy.Parse(relativePath, allowRoot: false);
            return new FileSystemLogicalPath(rootName, parsed.Value);
        }
        catch (FileSystemOperationException exception)
            when (exception.Code == FileSystemErrorCodes.InvalidPath)
        {
            throw InvalidPath(exception);
        }
    }

    private static FileSystemOperationException InvalidPath(
        Exception? innerException = null) =>
        new(
            FileSystemErrorCodes.FileSystemPathInvalid,
            "Логический путь файловой системы имеет недопустимую форму.",
            innerException);
}

internal sealed record ResolvedFileSystemPath(
    FileSystemLogicalPath Path,
    IRootedFileSystem FileSystem);

internal sealed class FileSystemRootRegistry : IDisposable
{
    private readonly Dictionary<string, RootedFileSystemProvider> roots;
    private bool disposed;

    private FileSystemRootRegistry(
        FileSystemRegistryState state,
        Dictionary<string, RootedFileSystemProvider>? roots = null)
    {
        State = state;
        this.roots = roots ?? new Dictionary<string, RootedFileSystemProvider>(StringComparer.Ordinal);
    }

    internal FileSystemRegistryState State { get; }

    internal IReadOnlyList<string> RootNames =>
        roots.Keys.Order(StringComparer.Ordinal).ToArray();

    internal string DiagnosticState
    {
        get
        {
            if (State == FileSystemRegistryState.NotConfigured)
            {
                return "not_configured";
            }

            if (State == FileSystemRegistryState.ConfigurationInvalid)
            {
                return "configuration_invalid";
            }

            var available = roots.Values.Count(provider => provider.IsAvailable);
            if (available == 0)
            {
                return "unavailable";
            }

            return available == roots.Count ? "available" : "degraded";
        }
    }

    internal static FileSystemRootRegistry Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("WebAssistant:FileSystem");
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            return new FileSystemRootRegistry(FileSystemRegistryState.NotConfigured);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = new Dictionary<string, RootedFileSystemProvider>(StringComparer.Ordinal);
        try
        {
            foreach (var child in children)
            {
                var name = child.Key;
                var physicalPath = child.Value;
                if (!IsValidRootName(name) ||
                    !names.Add(name) ||
                    child.GetChildren().Any() ||
                    !IsValidAbsolutePhysicalPath(physicalPath))
                {
                    DisposeAll(configured.Values);
                    return new FileSystemRootRegistry(FileSystemRegistryState.ConfigurationInvalid);
                }

                configured.Add(
                    name,
                    new RootedFileSystemProvider(Path.GetFullPath(physicalPath!.Trim())));
            }

            return new FileSystemRootRegistry(
                FileSystemRegistryState.Configured,
                configured);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            DisposeAll(configured.Values);
            return new FileSystemRootRegistry(FileSystemRegistryState.ConfigurationInvalid);
        }
    }

    internal ResolvedFileSystemPath Resolve(string? path, bool allowRoot)
    {
        ThrowIfDisposed();
        EnsureConfigured();

        var logicalPath = FileSystemLogicalPath.Parse(path, allowRoot);
        if (!roots.TryGetValue(logicalPath.RootName, out var provider))
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemRootNotFound,
                $"Логический корень '{logicalPath.RootName}' не настроен.");
        }

        if (!provider.TryGetFileSystem(out var fileSystem) || fileSystem is null)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemRootUnavailable,
                $"Логический корень '{logicalPath.RootName}' временно недоступен.");
        }

        return new ResolvedFileSystemPath(logicalPath, fileSystem);
    }

    internal void EnsureConfigured()
    {
        ThrowIfDisposed();
        if (State == FileSystemRegistryState.NotConfigured)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemNotConfigured,
                "Файловая подсистема не настроена.");
        }

        if (State == FileSystemRegistryState.ConfigurationInvalid)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemConfigurationInvalid,
                "Конфигурация файловой подсистемы некорректна.");
        }
    }

    internal static bool IsValidRootName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        return name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DisposeAll(roots.Values);
        roots.Clear();
    }

    private static bool IsValidAbsolutePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed) &&
                Path.GetFullPath(trimmed).Length > 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static void DisposeAll(IEnumerable<RootedFileSystemProvider> providers)
    {
        foreach (var provider in providers)
        {
            provider.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
