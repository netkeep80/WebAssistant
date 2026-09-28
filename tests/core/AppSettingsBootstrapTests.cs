using System.Text;
using Microsoft.Extensions.Configuration;
using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class AppSettingsBootstrapTests : IDisposable
{
    private readonly string root;

    public AppSettingsBootstrapTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-appsettings-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Normalize_NativeDrivePath_EscapesBackslashesBeforeJsonParsing()
    {
        var source = """
            {
              // comment must stay valid
              "path": "C:\new\test\folder",
              "message": "line\nnext",
              "unicode": "\u0410"
            }
            """;

        var normalized = NativeWindowsPathJsonPreprocessor.Normalize(
            source,
            out var changed);

        Assert.True(changed);
        Assert.Contains(
            @"""path"": ""C:\\new\\test\\folder""",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains(
            @"""message"": ""line\nnext""",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains(
            @"""unicode"": ""\u0410""",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains("// comment must stay valid", normalized);

        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(normalized));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        Assert.Equal(
            @"C:\new\test\folder",
            configuration["path"]);
        Assert.Equal(
            "line\nnext",
            configuration["message"]);
        Assert.Equal(
            "А",
            configuration["unicode"]);
    }

    [Fact]
    public void Normalize_NativeUncPath_ProducesJsonEncodedUnc()
    {
        var source = """
            {
              "path": "\\server\share\new\test"
            }
            """;

        var normalized = NativeWindowsPathJsonPreprocessor.Normalize(
            source,
            out var changed);

        Assert.True(changed);

        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(normalized));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        Assert.Equal(
            @"\\server\share\new\test",
            configuration["path"]);
    }

    [Fact]
    public void Normalize_AlreadyEscapedAndForwardSlashPaths_AreUnchanged()
    {
        var source = """
            {
              "escaped": "C:\\Program Files\\Example App",
              "forward": "C:/Program Files/Example App"
            }
            """;

        var normalized = NativeWindowsPathJsonPreprocessor.Normalize(
            source,
            out var changed);

        Assert.False(changed);
        Assert.Same(source, normalized);
    }

    [Fact]
    public void Normalize_QuotedPathInsideComments_IsNotModified()
    {
        var source = """
            {
              // "path": "C:\comment\only"
              /* "other": "D:\comment\only" */
              "value": "plain"
            }
            """;

        var normalized = NativeWindowsPathJsonPreprocessor.Normalize(
            source,
            out var changed);

        Assert.False(changed);
        Assert.Same(source, normalized);
    }

    [Fact]
    public void Prepare_UsesShadowCopyWithoutRewritingSourceAppsettings()
    {
        var source = """
            {
              // native Windows path pasted as-is
              "WebAssistant": {
                "FileSystem": {
                  "exchange": "C:\new\test"
                }
              }
            }
            """;
        var sourcePath = Path.Combine(root, "appsettings.json");
        File.WriteAllText(sourcePath, source);

        using var bootstrap = AppSettingsBootstrap.Prepare(
            Array.Empty<string>(),
            root);
        var builder = bootstrap.CreateBuilder(
            Array.Empty<string>());

        Assert.True(bootstrap.IsPreprocessed);
        Assert.Equal(
            @"C:\new\test",
            builder.Configuration[
                "WebAssistant:FileSystem:exchange"]);
        Assert.Equal(
            source,
            File.ReadAllText(sourcePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
