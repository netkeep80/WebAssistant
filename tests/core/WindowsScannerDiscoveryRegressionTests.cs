using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class WindowsScannerDiscoveryRegressionTests
{
    [Fact]
    public async Task Discovery_OneDeviceCapsFailurePreservesOtherDeviceFromSameBackend()
    {
        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => Task.FromResult(driver switch
            {
                Driver.Wia => new List<ScanDevice>(),
                Driver.Twain => new List<ScanDevice>
                {
                    new(Driver.Twain, "twain-good-native", "TWAIN good scanner"),
                    new(Driver.Twain, "twain-offline-native", "TWAIN offline scanner")
                },
                _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
            }),
            (device, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (device.ID == "twain-offline-native")
                {
                    return Task.FromException<ScanCaps>(
                        new InvalidOperationException("offline network scanner capability probe failed"));
                }

                return Task.FromResult(new ScanCaps
                {
                    PaperSourceCaps = new PaperSourceCaps
                    {
                        SupportsFlatbed = true,
                        SupportsFeeder = false,
                        SupportsDuplex = false
                    }
                });
            });

        Assert.True(result.IsAvailable);
        var scanner = Assert.Single(result.Scanners);
        Assert.Equal(ScannerBackend.Twain, scanner.Backend);
        Assert.Equal("TWAIN good scanner", scanner.Name);
        Assert.Equal(
            ScannerIdentity.Create(ScannerBackend.Twain, "twain-good-native"),
            scanner.Id);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ScannerBackend.Twain, warning.Backend);
        Assert.Equal("enumerationFailed", warning.Code);
    }
}

#pragma warning restore CA2252
