using System.Text.RegularExpressions;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemPageContractTests
{
    [Fact]
    public void DedicatedFilesystemPage_IsLinkedFromDiagnosticsAndContainsNavigationControls()
    {
        var root = FindRepositoryRoot();
        var pagePath = Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "filesystem.html");
        Assert.True(File.Exists(pagePath), "filesystem.html должен существовать как отдельная diagnostic page.");

        var page = File.ReadAllText(pagePath);
        var index = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("href=\"/filesystem.html\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-breadcrumb\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-root\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-up\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-refresh\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-table\"", page, StringComparison.Ordinal);
        Assert.Contains("let currentPath", page, StringComparison.Ordinal);
        Assert.Contains("Root", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedFilesystemPage_ExposesAllMvpOperationsThroughPublicApiOnly()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("/v1/filesystem/list", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/file", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/directory", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/move", page, StringComparison.Ordinal);

        Assert.Contains("id=\"filesystem-create-directory\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-upload\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-upload-input\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"open\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"download\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"rename\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"move\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"delete\"", page, StringComparison.Ordinal);

        var apiLiterals = Regex.Matches(page, "[\\\"'`](/v1/[^\\\"'`]+)[\\\"'`]")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(apiLiterals);
        Assert.All(
            apiLiterals,
            value => Assert.StartsWith("/v1/filesystem/", value, StringComparison.Ordinal));
    }

    [Fact]
    public void DedicatedFilesystemPage_NeverEmbedsOrPreviewsUserFiles()
    {
        var page = ReadFilesystemPage();

        Assert.DoesNotContain("<object", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RootDirectory", page, StringComparison.Ordinal);
        Assert.DoesNotContain("file://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\\\", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DedicatedFilesystemPage_VisiblyHandlesRestrictionsAndPagination()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("restrictionCode", page, StringComparison.Ordinal);
        Assert.Contains("nextCursor", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-load-more\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-status\"", page, StringComparison.Ordinal);
        Assert.Contains("disabled", page, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadFilesystemPage()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "filesystem.html");
        Assert.True(File.Exists(path), "filesystem.html должен существовать.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден repository root WebAssistant.");
    }
}
