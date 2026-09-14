using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class WindowsScannerDiscoveryRegressionTests
{
    [Fact]
    public async Task Discovery_ListingDoesNotProbeCapabilities()
    {
        var capabilityCalls = 0;

        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => Task.FromResult(driver switch
            {
                Driver.Wia => new List<ScanDevice>
                {
                    new(Driver.Wia, "wia-registered-1", "Registered WIA scanner")
                },
                Driver.Twain => new List<ScanDevice>
                {
                    new(Driver.Twain, "twain-registered-1", "Registered TWAIN scanner")
                },
                _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
            }),
            (_, _) =>
            {
                Interlocked.Increment(ref capabilityCalls);
                return Task.FromException<ScanCaps>(
                    new TimeoutException("capability probe must not run while listing"));
            });

        Assert.True(result.IsAvailable);
        Assert.Equal(2, result.Scanners.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, capabilityCalls);
        Assert.Contains(result.Scanners, scanner =>
            scanner.Backend == ScannerBackend.Wia && scanner.Name == "Registered WIA scanner");
        Assert.Contains(result.Scanners, scanner =>
            scanner.Backend == ScannerBackend.Twain && scanner.Name == "Registered TWAIN scanner");
    }
}

#pragma warning restore CA2252
