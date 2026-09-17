using System.Text.RegularExpressions;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemPageContractTests
{
    [Fact]
    public void DedicatedFilesystemPage_DeclaresMultiRootTwoPanelStructure()
    {
        var root = FindRepositoryRoot();
        var page = ReadFilesystemPage();
        var index = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "index.html"));

        Assert.Contains("href=\"/filesystem.html\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-roots\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-left\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-right\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-splitter\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-left-breadcrumb\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"filesystem-right-breadcrumb\"", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/roots", page, StringComparison.Ordinal);
        Assert.Contains("activeRoot", page, StringComparison.Ordinal);
        Assert.Contains("currentSide", page, StringComparison.Ordinal);
        Assert.Contains("selectedPath", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedFilesystemPage_UsesPublicApiAndIndependentPanelOperations()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("/v1/filesystem/list", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/file", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/directory", page, StringComparison.Ordinal);
        Assert.Contains("/v1/filesystem/move", page, StringComparison.Ordinal);

        foreach (var side in new[] { "left", "right" })
        {
            Assert.Contains($"id=\"filesystem-{side}-create-file\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-create-directory\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-upload\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-upload-input\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-refresh\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-table-wrap\"", page, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("filesystem-left-sort-key", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-right-sort-key", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-left-sort-direction", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-right-sort-direction", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"name\"", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"size\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-sort", page, StringComparison.Ordinal);

        Assert.Contains("data-action=\"move\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"rename\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"delete\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-action=\"open\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-action=\"download\"", page, StringComparison.Ordinal);

        var apiLiterals = Regex.Matches(page, "[\\\"'`](/v1/[^\\\"'`]+)[\\\"'`]")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(apiLiterals);
        Assert.All(
            apiLiterals,
            value => Assert.StartsWith("/v1/filesystem/", value, StringComparison.Ordinal));
    }

    [Fact]
    public void DedicatedFilesystemPage_UsesIconKindDisplayAndExactlyThreeRowActions()
    {
        var page = ReadFilesystemPage();

        Assert.DoesNotContain("<th>Тип</th>", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("icon-folder", page, StringComparison.Ordinal);
        Assert.Contains("icon-file", page, StringComparison.Ordinal);
        Assert.Contains("icon-restricted", page, StringComparison.Ordinal);
        Assert.Contains("entry-kind-directory", page, StringComparison.Ordinal);
        Assert.Contains("entry-kind-file", page, StringComparison.Ordinal);
        Assert.Contains("entry-restricted", page, StringComparison.Ordinal);
        Assert.Contains("aria-label", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Переместить вправо", page, StringComparison.Ordinal);
        Assert.Contains("Переместить влево", page, StringComparison.Ordinal);
        Assert.Contains("Переименовать", page, StringComparison.Ordinal);
        Assert.Contains("Удалить", page, StringComparison.Ordinal);
        Assert.Contains("createRowActions", page, StringComparison.Ordinal);
        Assert.Contains("..", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedFilesystemPage_ImplementsOneStepUploadHeaderSortingAndStaleResponseProtection()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("showPicker", page, StringComparison.Ordinal);
        Assert.Contains("change", page, StringComparison.Ordinal);
        Assert.Contains("generation", page, StringComparison.Ordinal);
        Assert.Contains("fullPath", page, StringComparison.Ordinal);
        Assert.Contains("sortKey", page, StringComparison.Ordinal);
        Assert.Contains("sortDirection", page, StringComparison.Ordinal);
        Assert.Contains("setSort", page, StringComparison.Ordinal);
        Assert.Contains("createdAt", page, StringComparison.Ordinal);
        Assert.Contains("lastModifiedAt", page, StringComparison.Ordinal);
        Assert.Contains("restrictionCode", page, StringComparison.Ordinal);
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
