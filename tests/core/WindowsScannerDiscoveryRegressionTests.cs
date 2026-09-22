using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Discovery_LogsPerBackendGetDeviceListOutcomeDurationWithoutNativeIds()
    {
        var logger = new CaptureLogger();
        const string nativeId = "machine-specific-native-id";
        const string backendFailureMessage = "twain enumeration boom";

        var result = await WindowsScanAdapter.DiscoverAsync(
            driver => driver switch
            {
                Driver.Wia => Task.FromResult(new List<ScanDevice>
                {
                    new(Driver.Wia, nativeId, "Registered WIA scanner")
                }),
                Driver.Twain => Task.FromException<List<ScanDevice>>(
                    new InvalidOperationException(backendFailureMessage)),
                _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
            },
            logger);

        Assert.True(result.IsAvailable);
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                "scanner.discovery backend=wia stage=getDeviceList outcome=success",
                StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal) &&
            message.Contains("deviceCount=1", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                "scanner.discovery backend=twain stage=getDeviceList outcome=failure",
                StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal) &&
            message.Contains("exceptionType=InvalidOperationException", StringComparison.Ordinal) &&
            message.Contains("hresult=0x", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(nativeId, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(backendFailureMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedCapabilities_LogsSelectedBackendStagesWithOpaqueScannerIdOnly()
    {
        var logger = new CaptureLogger();
        const string nativeId = "machine-specific-selected-native-id";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, nativeId);

        var scanner = await WindowsScanAdapter.ResolveCapabilitiesAsync(
            scannerId,
            driver => Task.FromResult(driver == Driver.Wia
                ? new List<ScanDevice>
                {
                    new(Driver.Wia, nativeId, "Selected WIA scanner")
                }
                : throw new InvalidOperationException("unrelated backend must not run")),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ScanCaps
                {
                    PaperSourceCaps = new PaperSourceCaps
                    {
                        SupportsFlatbed = true
                    }
                });
            },
            logger);

        Assert.NotNull(scanner);
        Assert.Equal(ScannerCapabilityState.Complete, scanner.CapabilityState);
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                $"scanner.capabilities backend=wia scannerId={scannerId} stage=getDeviceList outcome=success",
                StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                $"scanner.capabilities backend=wia scannerId={scannerId} stage=getCaps outcome=success",
                StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(nativeId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedCapabilities_GetCapsFailureReturnsUnavailableAndLogsExceptionDetailsWithoutNativeId()
    {
        var logger = new CaptureLogger();
        const string nativeId = "machine-specific-failing-native-id";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, nativeId);

        var scanner = await WindowsScanAdapter.ResolveCapabilitiesAsync(
            scannerId,
            driver => Task.FromResult(driver == Driver.Wia
                ? new List<ScanDevice>
                {
                    new(Driver.Wia, nativeId, "Selected WIA scanner")
                }
                : throw new InvalidOperationException("unrelated backend must not run")),
            (_, _) => Task.FromException<ScanCaps>(
                new TimeoutException("selected capability timeout")),
            logger);

        Assert.NotNull(scanner);
        Assert.Equal(ScannerCapabilityState.Unavailable, scanner.CapabilityState);
        Assert.Null(scanner.Capabilities);
        Assert.Contains(logger.Messages, message =>
            message.Contains(
                $"scanner.capabilities backend=wia scannerId={scannerId} stage=getCaps outcome=failure",
                StringComparison.Ordinal) &&
            message.Contains("durationMs=", StringComparison.Ordinal) &&
            message.Contains("exceptionType=TimeoutException", StringComparison.Ordinal) &&
            message.Contains("hresult=0x", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(
                "selected capability timeout",
                StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(nativeId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedCapabilities_GetCapsFailureRedactsNativeIdBeforeMessageTruncation()
    {
        var logger = new CaptureLogger();
        const string nativeId = "machine-specific-boundary-native-id";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, nativeId);
        var boundaryMessage = new string('x', 492) + nativeId;

        var scanner = await WindowsScanAdapter.ResolveCapabilitiesAsync(
            scannerId,
            driver => Task.FromResult(driver == Driver.Wia
                ? new List<ScanDevice>
                {
                    new(Driver.Wia, nativeId, "Selected WIA scanner")
                }
                : throw new InvalidOperationException("unrelated backend must not run")),
            (_, _) => Task.FromException<ScanCaps>(
                new InvalidOperationException(boundaryMessage)),
            logger);

        Assert.NotNull(scanner);
        Assert.Equal(ScannerCapabilityState.Unavailable, scanner.CapabilityState);
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(nativeId[..8], StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

#pragma warning restore CA2252
