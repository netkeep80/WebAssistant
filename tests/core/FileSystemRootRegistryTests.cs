using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemRootRegistryTests : IDisposable
{
    private readonly string tempRoot;
    private readonly string archiveRoot;
    private readonly string nfsRoot;

    public FileSystemRootRegistryTests()
    {
        tempRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-root-registry-tests",
            Guid.NewGuid().ToString("N"));
        archiveRoot = Path.Combine(tempRoot, "archive");
        nfsRoot = Path.Combine(tempRoot, "nfs");
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(nfsRoot);
    }

    [Fact]
    public void Load_EmptyFileSystemSection_IsNotConfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        using var registry = FileSystemRootRegistry.Load(configuration);

        Assert.Equal(FileSystemRegistryState.NotConfigured, registry.State);
        Assert.Empty(registry.RootNames);
    }

    [Fact]
    public void Load_CaseOnlyRootNames_AreRejectedAsConfigurationCollision()
    {
        var configuration = new FixedConfiguration(
            new FixedSection("archive", archiveRoot),
            new FixedSection("Archive", nfsRoot));

        using var registry = FileSystemRootRegistry.Load(configuration);

        Assert.Equal(FileSystemRegistryState.ConfigurationInvalid, registry.State);
        Assert.Empty(registry.RootNames);
    }

    [Theory]
    [InlineData("archive/reports/2026", "archive", "reports/2026")]
    [InlineData("A-1/file.bin", "A-1", "file.bin")]
    [InlineData("root_1/file.bin", "root_1", "file.bin")]
    [InlineData("root.name/file.bin", "root.name", "file.bin")]
    public void LogicalPath_ParsesRootAndRelativePath(
        string value,
        string expectedRoot,
        string expectedRelativePath)
    {
        var parsed = FileSystemLogicalPath.Parse(value, allowRoot: false);

        Assert.Equal(expectedRoot, parsed.RootName);
        Assert.Equal(expectedRelativePath, parsed.RelativePath);
        Assert.Equal(value, parsed.Value);
    }

    [Theory]
    [InlineData("archive/../outside")]
    [InlineData("archive/./file.bin")]
    [InlineData("archive\\file.bin")]
    [InlineData("/archive/file.bin")]
    [InlineData("./file.bin")]
    [InlineData("../file.bin")]
    [InlineData("bad:name/file.bin")]
    public void LogicalPath_RejectsInvalidOrEscapingForms(string value)
    {
        var exception = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemLogicalPath.Parse(value, allowRoot: false));

        Assert.Equal(FileSystemErrorCodes.FileSystemPathInvalid, exception.Code);
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

    private sealed class FixedConfiguration : IConfiguration
    {
        private readonly IConfigurationSection fileSystem;

        internal FixedConfiguration(params FixedSection[] roots)
        {
            fileSystem = new FixedSection(
                "FileSystem",
                null,
                "WebAssistant:FileSystem",
                roots);
        }

        public string? this[string key]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() =>
            Array.Empty<IConfigurationSection>();

        public IChangeToken GetReloadToken() =>
            new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) =>
            string.Equals(key, "WebAssistant:FileSystem", StringComparison.Ordinal)
                ? fileSystem
                : FixedSection.Empty(key);
    }

    private sealed class FixedSection : IConfigurationSection
    {
        private readonly IReadOnlyList<IConfigurationSection> children;

        internal FixedSection(string key, string? value)
            : this(key, value, key, Array.Empty<IConfigurationSection>())
        {
        }

        internal FixedSection(
            string key,
            string? value,
            string path,
            IReadOnlyList<IConfigurationSection> children)
        {
            Key = key;
            Value = value;
            Path = path;
            this.children = children;
        }

        internal static FixedSection Empty(string path) =>
            new(path.Split(':').Last(), null, path, Array.Empty<IConfigurationSection>());

        public string Key { get; }

        public string Path { get; }

        public string? Value { get; set; }

        public string? this[string key]
        {
            get => GetSection(key).Value;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => children;

        public IChangeToken GetReloadToken() =>
            new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) =>
            children.FirstOrDefault(child =>
                string.Equals(child.Key, key, StringComparison.Ordinal)) ??
            Empty(string.Concat(Path, ":", key));
    }
}
