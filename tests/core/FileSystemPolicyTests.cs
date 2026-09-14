using WebAssistant.FileSystem;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemPolicyTests
{
    [Fact]
    public void Parse_Root_IsAllowedOnlyForRootAwareOperations()
    {
        var root = FileSystemPathPolicy.Parse(string.Empty, allowRoot: true);

        Assert.Empty(root.Segments);
        Assert.Equal(string.Empty, root.Value);

        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.Parse(string.Empty, allowRoot: false));
        Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("./file.txt")]
    [InlineData("folder/../outside.txt")]
    [InlineData("folder/./file.txt")]
    [InlineData("folder//file.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData(".webassistant-staging/file.txt")]
    [InlineData("folder/.webassistant-upload-123")]
    public void Parse_UnsafeOrReservedPath_IsRejected(string path)
    {
        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.Parse(path, allowRoot: false));

        Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
    }

    [Fact]
    public void Parse_Nul_IsRejected()
    {
        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.Parse("folder/file\0.txt", allowRoot: false));

        Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
    }

    [Fact]
    public void Parse_ValidPath_PreservesCanonicalSlashSegments()
    {
        var path = FileSystemPathPolicy.Parse("incoming/2026/report.pdf", allowRoot: false);

        Assert.Equal("incoming/2026/report.pdf", path.Value);
        Assert.Equal(new[] { "incoming", "2026", "report.pdf" }, path.Segments);
    }

    [Fact]
    public void Parse_OnLinux_BackslashRemainsNativeFilenameCharacter()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = FileSystemPathPolicy.Parse("incoming/back\\slash.txt", allowRoot: false);

        Assert.Equal(new[] { "incoming", "back\\slash.txt" }, path.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/name")]
    public void ValidateEntryName_InvalidSingleName_IsRejected(string name)
    {
        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.ValidateEntryName(name));

        Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
    }

    [Theory]
    [InlineData("payload.exe")]
    [InlineData("PAYLOAD.EXE")]
    [InlineData("document.pdf.exe")]
    [InlineData("script.ps1")]
    [InlineData("script.PSM1")]
    [InlineData("script.vbs")]
    [InlineData("script.js")]
    [InlineData("page.html")]
    [InlineData("image.svg")]
    [InlineData("install.msi")]
    [InlineData("run.sh")]
    [InlineData("launcher.desktop")]
    public void BlockedFileType_FinalExtension_IsRejected(string name)
    {
        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.EnsureFileTypeAllowed(name));

        Assert.Equal(FileSystemErrorCodes.BlockedFileType, error.Code);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("document.docx")]
    [InlineData("sheet.xlsx")]
    [InlineData("archive.zip")]
    [InlineData("photo.jpg")]
    [InlineData("data.bin")]
    [InlineData("README")]
    public void BlockedFileType_OpaqueNonBlockedFile_IsAllowed(string name)
    {
        FileSystemPathPolicy.EnsureFileTypeAllowed(name);
        Assert.Null(FileSystemPathPolicy.GetRestrictionCode(name));
    }

    [Fact]
    public void RestrictionCode_BlockedExternalFile_IsReportedWithoutParsingContent()
    {
        Assert.Equal(
            FileSystemErrorCodes.BlockedFileType,
            FileSystemPathPolicy.GetRestrictionCode("externally-created.Js"));
    }

    [Fact]
    public void ValidateEntryName_ReservedInternalPrefix_IsRejected()
    {
        var error = Assert.Throws<FileSystemOperationException>(() =>
            FileSystemPathPolicy.ValidateEntryName(".WebAssistant-upload-ABC"));

        Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
    }

    [Fact]
    public void ValidateEntryName_WindowsReservedDeviceNames_AreRejectedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var name in new[] { "CON", "nul.txt", "COM1", "LPT9.log" })
        {
            var error = Assert.Throws<FileSystemOperationException>(() =>
                FileSystemPathPolicy.ValidateEntryName(name));
            Assert.Equal(FileSystemErrorCodes.InvalidPath, error.Code);
        }
    }
}
