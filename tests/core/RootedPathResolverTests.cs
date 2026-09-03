using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class RootedPathResolverTests : IDisposable
{
    private readonly string root;
    private readonly string outside;

    public RootedPathResolverTests()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-root-tests",
            Guid.NewGuid().ToString("N"));
        root = Path.Combine(testRoot, "root");
        outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
    }

    [Fact]
    public void Resolve_ValidNestedRelativePath_StaysInsideRoot()
    {
        var resolver = new RootedPathResolver(root);

        var resolved = resolver.Resolve("documents/report.pdf");

        var expected = Path.Combine(root, "documents", "report.pdf");
        Assert.Equal(Path.GetFullPath(expected), resolved);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("./file.txt")]
    [InlineData("folder/../file.txt")]
    [InlineData("folder\\..\\file.txt")]
    public void Resolve_TraversalSegments_AreRejected(string relativePath)
    {
        var resolver = new RootedPathResolver(root);

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(relativePath));
    }

    [Theory]
    [InlineData("/outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("\\\\server\\share\\outside.txt")]
    public void Resolve_RootedOrForeignAbsolutePath_AreRejected(string path)
    {
        var resolver = new RootedPathResolver(root);

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(path));
    }

    [Fact]
    public void Resolve_ExistingSymlinkEscape_IsRejected()
    {
        var link = Path.Combine(root, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var resolver = new RootedPathResolver(root);

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve("escape/document.txt"));
    }

    [Fact]
    public void Resolve_RootItselfAsSymlink_IsRejected()
    {
        var linkedRoot = Path.Combine(Path.GetDirectoryName(root)!, "linked-root");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() =>
            new RootedPathResolver(linkedRoot));
    }

    public void Dispose()
    {
        var testRoot = Directory.GetParent(root)!.FullName;
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch
        {
        }
    }
}
