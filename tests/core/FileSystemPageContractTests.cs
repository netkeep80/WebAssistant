using System.Text.RegularExpressions;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemPageContractTests
{
    [Fact]
    public void DedicatedFilesystemPage_DeclaresMultiRootTwoPanelSelectionStructure()
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
        Assert.Contains("activeRoot", page, StringComparison.Ordinal);
        Assert.Contains("currentSide", page, StringComparison.Ordinal);
        Assert.Contains("selectedNames", page, StringComparison.Ordinal);
        Assert.Contains("selectionAnchor", page, StringComparison.Ordinal);
        Assert.Contains("currentRow", page, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedPath", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedFilesystemPage_DeclaresExact230PublicApiAndPostOnlyMutations()
    {
        var page = ReadFilesystemPage();

        foreach (var route in new[]
        {
            "/v1/filesystem/roots",
            "/v1/filesystem/list",
            "/v1/filesystem/file",
            "/v1/filesystem/files",
            "/v1/filesystem/find",
            "/v1/filesystem/file/delete",
            "/v1/filesystem/directory",
            "/v1/filesystem/directory/delete",
            "/v1/filesystem/move",
            "/v1/filesystem/directory/move",
            "/v1/filesystem/rename"
        })
        {
            Assert.Contains(route, page, StringComparison.Ordinal);
        }

        Assert.Contains("fileDelete", page, StringComparison.Ordinal);
        Assert.Contains("directoryDelete", page, StringComparison.Ordinal);
        Assert.Contains("directoryMove", page, StringComparison.Ordinal);
        Assert.Contains("method:\"POST\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("method:\"PUT\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("method:\"DELETE\"", page, StringComparison.Ordinal);

        var apiLiterals = Regex.Matches(page, "[\\\"'`](/v1/[^\\\"'`]+)[\\\"'`]")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(apiLiterals);
        Assert.All(
            apiLiterals,
            value => Assert.StartsWith("/v1/filesystem/", value, StringComparison.Ordinal));
    }

    [Fact]
    public void DedicatedFilesystemPage_DeclaresWildcardZipFindAndIndependentDesktopSelection()
    {
        var page = ReadFilesystemPage();

        foreach (var side in new[] { "left", "right" })
        {
            Assert.Contains($"id=\"filesystem-{side}-wildcard\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-apply-wildcard\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-zip\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-find-names\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-find\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-move-selected\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-create-file\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-create-directory\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-upload\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-upload-input\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-refresh\"", page, StringComparison.Ordinal);
            Assert.Contains($"id=\"filesystem-{side}-table-wrap\"", page, StringComparison.Ordinal);
        }

        Assert.Contains("value=\"*.*\"", page, StringComparison.Ordinal);
        Assert.Contains("ctrlKey", page, StringComparison.Ordinal);
        Assert.Contains("shiftKey", page, StringComparison.Ordinal);
        Assert.Contains("selected-row", page, StringComparison.Ordinal);
        Assert.Contains("current-row", page, StringComparison.Ordinal);
        Assert.Contains("findMarks", page, StringComparison.Ordinal);
        Assert.Contains("surviving", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DedicatedFilesystemPage_SeparatesBatchFileMoveDirectoryMoveAndRename()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("moveSelected", page, StringComparison.Ordinal);
        Assert.Contains("fileNames", page, StringComparison.Ordinal);
        Assert.Contains("moveDirectory", page, StringComparison.Ordinal);
        Assert.Contains("renameEntry", page, StringComparison.Ordinal);
        Assert.Contains("api.directoryMove", page, StringComparison.Ordinal);
        Assert.Contains("api.rename", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"move\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"rename\"", page, StringComparison.Ordinal);
        Assert.Contains("data-action=\"delete\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("destinationPath:targetPath", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedFilesystemPage_Preserves227ErgonomicsAndNoPreviewPolicy()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("showPicker", page, StringComparison.Ordinal);
        Assert.Contains("generation", page, StringComparison.Ordinal);
        Assert.Contains("sortKey", page, StringComparison.Ordinal);
        Assert.Contains("sortDirection", page, StringComparison.Ordinal);
        Assert.Contains("setSort", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"name\"", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"size\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-sort", page, StringComparison.Ordinal);
        Assert.Contains("ArrowUp", page, StringComparison.Ordinal);
        Assert.Contains("ArrowDown", page, StringComparison.Ordinal);
        Assert.Contains("Enter", page, StringComparison.Ordinal);
        Assert.Contains("Tab", page, StringComparison.Ordinal);
        Assert.Contains("dragstart", page, StringComparison.Ordinal);
        Assert.Contains("drop", page, StringComparison.Ordinal);
        Assert.Contains("..", page, StringComparison.Ordinal);
        Assert.Contains("icon-folder", page, StringComparison.Ordinal);
        Assert.Contains("icon-file", page, StringComparison.Ordinal);
        Assert.Contains("icon-restricted", page, StringComparison.Ordinal);

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
