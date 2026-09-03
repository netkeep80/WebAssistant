using System.Text;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxVirtualScanAdapterTests
{
    [Fact]
    [Trait("Category", "LinuxVirtualScanner")]
    public async Task VirtualSaneScanner_ProducesPdfThroughDirectSdkProductAdapter()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_LINUX_VIRTUAL") != "1")
        {
            return;
        }

        var tempDirectory = Path.GetTempPath();
        var before = Directory
            .GetFiles(tempDirectory, "webassistant-*.pdf")
            .ToHashSet(StringComparer.Ordinal);

        using var adapter = new LinuxScanAdapter();
        var devices = await adapter.GetScannersAsync();
        var virtualDevices = devices
            .Where(device => device.Name.Contains(
                "frontend-tester",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(virtualDevices);
        Assert.All(
            virtualDevices,
            device => Assert.StartsWith("test:", device.Id, StringComparison.Ordinal));
        Assert.Equal(
            virtualDevices.Length,
            virtualDevices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count());

        var scanner = Assert.Single(
            virtualDevices,
            device => string.Equals(device.Id, "test:0", StringComparison.Ordinal));

        await using var pdf = await adapter.ScanAsync(scanner.Id, ScanSource.Glass);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        Assert.True(bytes.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

        var leaked = Directory
            .GetFiles(tempDirectory, "webassistant-*.pdf")
            .Where(path => !before.Contains(path))
            .ToArray();
        Assert.Empty(leaked);
    }
}
