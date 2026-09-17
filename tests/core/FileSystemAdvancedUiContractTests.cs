using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemAdvancedUiContractTests
{
    [Fact]
    public void FilesystemPage_UsesViewportSplitLayoutWithoutHorizontalScrolling()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("id=\"filesystem-splitter\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"separator\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-orientation=\"vertical\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-valuemin=\"20\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-valuemax=\"80\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-valuenow=\"50\"", page, StringComparison.Ordinal);
        Assert.Contains("col-resize", page, StringComparison.Ordinal);
        Assert.Contains("pointermove", page, StringComparison.Ordinal);
        Assert.Contains("setPointerCapture", page, StringComparison.Ordinal);
        Assert.Contains("overflow-x:hidden", page.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("table-layout:fixed", page.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilesystemPage_SortsByColumnHeaderWithoutLegacySortControls()
    {
        var page = ReadFilesystemPage();

        Assert.DoesNotContain("filesystem-left-sort-key", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-right-sort-key", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-left-sort-direction", page, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem-right-sort-direction", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"name\"", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"size\"", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"createdAt\"", page, StringComparison.Ordinal);
        Assert.Contains("data-sort-key=\"lastModifiedAt\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-sort", page, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesystemPage_DeclaresKeyboardSelectionAndDragDropBehavior()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("currentSide", page, StringComparison.Ordinal);
        Assert.Contains("selectedPath", page, StringComparison.Ordinal);
        Assert.Contains("ArrowUp", page, StringComparison.Ordinal);
        Assert.Contains("ArrowDown", page, StringComparison.Ordinal);
        Assert.Contains("Enter", page, StringComparison.Ordinal);
        Assert.Contains("Tab", page, StringComparison.Ordinal);
        Assert.Contains("dragstart", page, StringComparison.Ordinal);
        Assert.Contains("dragover", page, StringComparison.Ordinal);
        Assert.Contains("drop", page, StringComparison.Ordinal);
        Assert.Contains("dataTransfer", page, StringComparison.Ordinal);
        Assert.Contains("draggable", page, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesystemPage_RootSwitchExplicitlyClearsTransientDragState()
    {
        var page = ReadFilesystemPage();

        Assert.Contains("function resetTransientDragState", page, StringComparison.Ordinal);
        Assert.Contains("internalDrag=null", page.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("clearDropClasses()", page, StringComparison.Ordinal);
        Assert.Contains("resetTransientDragState();", page, StringComparison.Ordinal);
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
