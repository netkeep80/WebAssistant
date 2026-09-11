using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

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
    public async Task Discovery_EnumeratesWiaAndTwainAndNormalizesCapabilities()
    {
        var calls = new List<Driver>();
        var result = await WindowsScanAdapter.DiscoverAsync(
            driver =>
            {
                calls.Add(driver);
                return Task.FromResult(driver switch
                {
                    Driver.Wia => new List<ScanDevice>
                    {
                        new(Driver.Wia, "wia-native-1", "WIA scanner")
                    },
                    Driver.Twain => new List<ScanDevice>
                    {
                        new(Driver.Twain, "twain-native-1", "TWAIN scanner")
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
                });
            },
            (device, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ScanCaps
                {
                    PaperSourceCaps = device.Driver == Driver.Wia
                        ? new PaperSourceCaps
                        {
                            SupportsFlatbed = true,
                            SupportsFeeder = true,
                            SupportsDuplex = true,
                            FeederHasPaper = true
                        }
                        : new PaperSourceCaps
                        {
                            SupportsFlatbed = true,
                            SupportsFeeder = false,
                            SupportsDuplex = false,
                            FeederHasPaper = null
                        }
                });
            });

        Assert.Equal(new[] { Driver.Wia, Driver.Twain }, calls);
        Assert.True(result.IsAvailable);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Scanners.Count);

        var wia = Assert.Single(result.Scanners, scanner => scanner.Backend == ScannerBackend.Wia);
        Assert.Equal(ScannerIdentity.Create(ScannerBackend.Wia, "wia-native-1"), wia.Id);
        Assert.Equal("WIA scanner", wia.Name);
        Assert.True(wia.SupportsFlatbed);
        Assert.True(wia.SupportsFeeder);
        Assert.True(wia.SupportsDuplex);
        Assert.Equal(FeederPaperState.Present, wia.FeederPaperState);

        var twain = Assert.Single(result.Scanners, scanner => scanner.Backend == ScannerBackend.Twain);
        Assert.Equal(ScannerIdentity.Create(ScannerBackend.Twain, "twain-native-1"), twain.Id);
        Assert.Equal("TWAIN scanner", twain.Name);
        Assert.True(twain.SupportsFlatbed);
        Assert.False(twain.SupportsFeeder);
        Assert.False(twain.SupportsDuplex);
        Assert.Equal(FeederPaperState.Unknown, twain.FeederPaperState);
    }

    [Fact]
    public async Task Discovery_OneBackendFailurePreservesSuccessfulBackendAndWarning()
    {
        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => driver == Driver.Wia
                ? Task.FromException<List<ScanDevice>>(new InvalidOperationException("wia unavailable"))
                : Task.FromResult(new List<ScanDevice>
                {
                    new(Driver.Twain, "twain-native-1", "TWAIN scanner")
                }),
            (_, _) => Task.FromResult(new ScanCaps
            {
                PaperSourceCaps = new PaperSourceCaps { SupportsFlatbed = true }
            }));

        Assert.True(result.IsAvailable);
        var scanner = Assert.Single(result.Scanners);
        Assert.Equal(ScannerBackend.Twain, scanner.Backend);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ScannerBackend.Wia, warning.Backend);
        Assert.Equal("enumerationFailed", warning.Code);
    }

    [Fact]
    public async Task Discovery_BothBackendFailuresMarksDiscoveryUnavailable()
    {
        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => Task.FromException<List<ScanDevice>>(
                new InvalidOperationException($"{driver} unavailable")),
            (_, _) => throw new InvalidOperationException("caps must not be queried"));

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Scanners);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning =>
            warning.Backend == ScannerBackend.Wia && warning.Code == "enumerationFailed");
        Assert.Contains(result.Warnings, warning =>
            warning.Backend == ScannerBackend.Twain && warning.Code == "enumerationFailed");
    }

    [Fact]
    public async Task Discovery_DuplicateNativeIdentityFailsClosedWithoutEnumerationIndex()
    {
        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => Task.FromResult(driver == Driver.Wia
                ? new List<ScanDevice>
                {
                    new(Driver.Wia, "duplicate-native", "First"),
                    new(Driver.Wia, "duplicate-native", "Second")
                }
                : []),
            (_, _) => Task.FromResult(new ScanCaps
            {
                PaperSourceCaps = new PaperSourceCaps { SupportsFlatbed = true }
            }));

        Assert.True(result.IsAvailable);
        Assert.Empty(result.Scanners);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ScannerBackend.Wia, warning.Backend);
        Assert.Equal("ambiguousNativeIdentity", warning.Code);
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
    public async Task VirtualTwainScanner_ProducesPdfFromStableIdAndExplicitFlatbedSource()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("WEBASSISTANT_WINDOWS_VIRTUAL") != "1")
        {
            return;
        }

        using var adapter = new WindowsScanAdapter();
        var discovery = await adapter.GetScannersAsync();
        var scanner = Assert.Single(
            discovery.Scanners,
            x => x.Backend == ScannerBackend.Twain && x.Name.Contains(
                "TWAIN2 Software Scanner",
                StringComparison.OrdinalIgnoreCase));

        Assert.StartsWith("wa1-twain-", scanner.Id, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await adapter.ScanAsync(
                ScannerIdentity.Create(ScannerBackend.Twain, "scanner-id-that-does-not-exist"));
        });

        await using var pdf = await adapter.ScanAsync(scanner.Id, ScanSource.Glass);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        Assert.True(bytes.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }
}

#pragma warning restore CA2252
