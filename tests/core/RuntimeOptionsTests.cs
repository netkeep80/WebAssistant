using System.Net;
using Microsoft.Extensions.Configuration;
using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void Listener_Default_IsLoopbackOnlyOnDefaultPort()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = WebAssistantRuntimeOptions.Load(configuration);

        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.NotEqual(IPAddress.Any, options.ListenAddress);
        Assert.NotEqual(IPAddress.IPv6Any, options.ListenAddress);
        Assert.Equal(WebAssistantRuntimeOptions.DefaultPort, options.Port);
        Assert.Equal(17654, options.Port);
    }

    [Fact]
    public void Listener_ConfiguredPort_PreservesLoopbackOnlyAddress()
    {
        var configuration = BuildConfiguration(
            ("WebAssistant:Port", "27654"));

        var options = WebAssistantRuntimeOptions.Load(configuration);

        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.NotEqual(IPAddress.Any, options.ListenAddress);
        Assert.NotEqual(IPAddress.IPv6Any, options.ListenAddress);
        Assert.Equal(27654, options.Port);
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:17654")]
    [InlineData("http_ports", "17654")]
    [InlineData("https_ports", "17655")]
    public void Listener_AlternativeTopLevelConfiguration_IsRejected(
        string key,
        string value)
    {
        var configuration = BuildConfiguration((key, value));

        Assert.Throws<InvalidOperationException>(() =>
            WebAssistantRuntimeOptions.Load(configuration));
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Lan:Url", "http://192.168.1.20:17654")]
    [InlineData("Kestrel:Endpoints:Loopback:Url", "http://127.0.0.1:27654")]
    public void Listener_ArbitraryKestrelEndpoints_AreRejected(
        string key,
        string value)
    {
        var configuration = BuildConfiguration((key, value));

        Assert.Throws<InvalidOperationException>(() =>
            WebAssistantRuntimeOptions.Load(configuration));
    }

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

    private static IConfiguration BuildConfiguration(
        params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                item => item.Key,
                item => (string?)item.Value,
                StringComparer.OrdinalIgnoreCase))
            .Build();
    }
}
