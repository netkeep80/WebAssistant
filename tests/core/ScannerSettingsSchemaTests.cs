using System.Text.Json;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerSettingsSchemaTests
{
    [Fact]
    public void CanonicalScannerSettingsSchema_DefinesPortableFieldsAndDefaults()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "webassist", "docs", "scanner-settings.schema.json");

        Assert.True(File.Exists(path), $"Canonical scanner settings schema is missing: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = document.RootElement;
        var fields = rootElement.GetProperty("fields");

        Assert.Equal("integer", fields.GetProperty("dpi").GetProperty("type").GetString());
        Assert.Equal("dpi", fields.GetProperty("dpi").GetProperty("unit").GetString());
        Assert.Equal(100, fields.GetProperty("dpi").GetProperty("preferredDefault").GetInt32());

        Assert.Equal(
            new[] { "color", "grayscale", "blackAndWhite" },
            fields.GetProperty("colorMode").GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("color", fields.GetProperty("colorMode").GetProperty("preferredDefault").GetString());

        Assert.Equal(
            new[] { "letter", "legal", "a5", "a4", "a3", "b5", "b4" },
            fields.GetProperty("paperSize").GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("letter", fields.GetProperty("paperSize").GetProperty("preferredDefault").GetString());

        Assert.Equal("boolean", fields.GetProperty("duplex").GetProperty("type").GetString());
        Assert.False(fields.GetProperty("duplex").GetProperty("preferredDefault").GetBoolean());

        var json = rootElement.GetRawText();
        Assert.DoesNotContain("\"wia\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"twain\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sane\"", json, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "webassist",
                    "src",
                    "WebAssistant",
                    "WebAssistant.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}
