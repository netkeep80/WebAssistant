using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsScanAdapterTests
{
    [Fact]
    public void Constructor_OnNonWindows_FailsFast()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => new WindowsScanAdapter());
    }

    [Fact]
    public void BackendPolicy_PrefersWiaWhenWiaDevicesExist()
    {
        var method = typeof(WindowsScanAdapter).GetMethod(
            "SelectPreferredDriver",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var driver = method.Invoke(null, new object?[] { 1 });
        Assert.NotNull(driver);
        Assert.Equal("Wia", driver.ToString());
    }

    [Fact]
    public void BackendPolicy_FallsBackToTwainWhenWiaListIsEmpty()
    {
        var method = typeof(WindowsScanAdapter).GetMethod(
            "SelectPreferredDriver",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var driver = method.Invoke(null, new object?[] { 0 });
        Assert.NotNull(driver);
        Assert.Equal("Twain", driver.ToString());
    }

    [Theory]
    [InlineData("Glass", "Flatbed")]
    [InlineData("Feeder", "Feeder")]
    [InlineData("Duplex", "Duplex")]
    public void ScanSource_MapsToExactNaps2PaperSource(
        string sourceName,
        string expectedPaperSource)
    {
        var method = typeof(WindowsScanAdapter).GetMethod(
            "MapPaperSource",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var source = Enum.Parse<ScanSource>(sourceName);
        var paperSource = method.Invoke(null, new object?[] { source });
        Assert.NotNull(paperSource);
        Assert.Equal(expectedPaperSource, paperSource.ToString());
    }

    [Fact]
    [Trait("Category", "WindowsVirtualScanner")]
    public void ProductionCompositionRoot_ResolvesWindowsAdapter()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_WINDOWS_VIRTUAL") != "1")
        {
            return;
        }

        using var factory = new WebApplicationFactory<Program>();
        var adapter = factory.Services.GetRequiredService<IScanAdapter>();
        Assert.IsType<WindowsScanAdapter>(adapter);
    }

    [Fact]
    [Trait("Category", "WindowsVirtualScanner")]
    public async Task VirtualTwainScanner_ProducesPdfFromExplicitFlatbedSource()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_WINDOWS_VIRTUAL") != "1")
        {
            return;
        }

        using var adapter = new WindowsScanAdapter();
        var devices = await adapter.GetScannersAsync();
        var scanner = Assert.Single(
            devices,
            x => x.Name.Contains(
                "TWAIN2 Software Scanner",
                StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await adapter.ScanAsync("scanner-id-that-does-not-exist");
        });

        await using var pdf = await adapter.ScanAsync(scanner.Id, ScanSource.Glass);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        Assert.True(bytes.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }
}
