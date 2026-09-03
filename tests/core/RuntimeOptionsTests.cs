using Microsoft.Extensions.Configuration;
using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void FileSystemRoot_ConfiguredValue_IsCanonicalized()
    {
        var configured = Path.Combine(
            Path.GetTempPath(),
            "webassistant-configured-root",
            "data");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAssistant:FileSystem:RootDirectory"] = configured
            })
            .Build();

        var options = WebAssistantRuntimeOptions.Load(configuration);

        Assert.Equal(Path.GetFullPath(configured), options.FileSystemRootDirectory);
    }

    [Fact]
    public void FileSystemRoot_Default_IsPlatformSpecificServiceDataDirectory()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = WebAssistantRuntimeOptions.Load(configuration);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WebAssistant",
                    "data"),
                options.FileSystemRootDirectory);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/var/lib/webassistant", options.FileSystemRootDirectory);
        }
        else
        {
            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "data"),
                options.FileSystemRootDirectory);
        }
    }
}
