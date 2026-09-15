using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WebAssistant.Http;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerCapabilityBoundaryTests
{
    [Fact]
    public async Task ScannerList_ReturnsIdentityOnly_WithoutSourcesProjection()
    {
        var adapter = new CapabilityBoundaryFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        var scanners = document.RootElement.GetProperty("scanners").EnumerateArray().ToArray();
        Assert.Equal(2, scanners.Length);
        Assert.All(scanners, scanner =>
        {
            Assert.True(scanner.TryGetProperty("scannerId", out _));
            Assert.True(scanner.TryGetProperty("name", out _));
            Assert.True(scanner.TryGetProperty("backend", out _));
            Assert.False(scanner.TryGetProperty("sources", out _));
        });
        Assert.Equal(1, adapter.ListCalls);
        Assert.Equal(0, adapter.CapabilityCalls);
    }

    [Fact]
    public async Task SelectedScannerSettings_ProbesOnlySelectedScanner_WithoutGlobalListing()
    {
        var adapter = new CapabilityBoundaryFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();
        var selectedId = adapter.RegisteredScanners[1].Id;

        using var response = await client.GetAsync(
            $"/v1/scanners/{Uri.EscapeDataString(selectedId)}/settings");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, adapter.ListCalls);
        Assert.Equal(1, adapter.CapabilityCalls);
        Assert.Equal(selectedId, adapter.LastCapabilityScannerId);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(selectedId, document.RootElement.GetProperty("scannerId").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("modes").EnumerateArray());
    }

    [Fact]
    public async Task Scan_ProbesOnlySelectedScanner_WithoutGlobalListing()
    {
        var adapter = new CapabilityBoundaryFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();
        var selectedId = adapter.RegisteredScanners[1].Id;

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = selectedId,
            source = "flatbed",
            settings = new { duplex = false, dpi = 300, colorMode = "grayscale", paperSize = "a4" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, adapter.ListCalls);
        Assert.Equal(1, adapter.CapabilityCalls);
        Assert.Equal(selectedId, adapter.LastCapabilityScannerId);
        Assert.Equal(1, adapter.ScanCalls);
    }

    [Fact]
    public async Task SelectedScannerSettings_BackendFailure_DoesNotAttachRawDriverExceptionToLogger()
    {
        const string nativeSecret = "machine-specific-native-id";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, "registered-1");
        var adapter = new ThrowingCapabilityAdapter(
            new ScannerBackendUnavailableException(
                ScannerBackend.Wia,
                new InvalidOperationException(nativeSecret)));
        var logger = new CaptureLogger();

        await ScannerSettingsEndpointHandlers.GetAsync(
            adapter,
            scannerId,
            logger,
            CancellationToken.None);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(nativeSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedScannerSettings_UnexpectedCapabilityFailure_DoesNotAttachRawDriverExceptionToLogger()
    {
        const string nativeSecret = "machine-specific-capability-secret";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, "registered-1");
        var adapter = new ThrowingCapabilityAdapter(new TimeoutException(nativeSecret));
        var logger = new CaptureLogger();

        await ScannerSettingsEndpointHandlers.GetAsync(
            adapter,
            scannerId,
            logger,
            CancellationToken.None);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(nativeSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_AcquisitionFailure_DoesNotAttachRawDriverExceptionToLogger()
    {
        const string nativeSecret = "machine-specific-acquisition-secret";
        var scannerId = ScannerIdentity.Create(ScannerBackend.Wia, "registered-1");
        var adapter = new ThrowingScanAdapter(scannerId, new InvalidOperationException(nativeSecret));
        var logger = new CaptureLogger<ScanCoordinator>();
        var coordinator = new ScanCoordinator(logger);

        await coordinator.ExecuteAsync(
            adapter,
            new ScanRequest
            {
                ScannerId = scannerId,
                Source = "flatbed",
                Settings = new ScanSettings
                {
                    Duplex = false,
                    Dpi = 300,
                    ColorMode = "grayscale",
                    PaperSize = "a4"
                }
            },
            CancellationToken.None);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(nativeSecret, StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory(IScanAdapter adapter)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanAdapter>();
                services.AddSingleton(adapter);
            });
        });
    }

    private static ScannerDevice CreateCapableScanner(string scannerId)
    {
        var flatbed = new ScannerSourceCapabilities(
            [300],
            [ScannerColorMode.Grayscale],
            [ScannerPaperSize.A4]);
        return new ScannerDevice(
            scannerId,
            "Selected scanner",
            ScannerBackend.Wia,
            SupportsFlatbed: true,
            Capabilities: new ScannerEndpointCapabilities(flatbed, null, null));
    }

    private sealed class CapabilityBoundaryFakeAdapter : IScanAdapter
    {
        private int listCalls;
        private int capabilityCalls;
        private int scanCalls;

        internal CapabilityBoundaryFakeAdapter()
        {
            RegisteredScanners =
            [
                new ScannerDevice(
                    ScannerIdentity.Create(ScannerBackend.Wia, "registered-1"),
                    "Registered scanner 1",
                    ScannerBackend.Wia,
                    SupportsFlatbed: false),
                new ScannerDevice(
                    ScannerIdentity.Create(ScannerBackend.Wia, "registered-2"),
                    "Registered scanner 2",
                    ScannerBackend.Wia,
                    SupportsFlatbed: false)
            ];
        }

        internal IReadOnlyList<ScannerDevice> RegisteredScanners { get; }
        internal int ListCalls => Volatile.Read(ref listCalls);
        internal int CapabilityCalls => Volatile.Read(ref capabilityCalls);
        internal int ScanCalls => Volatile.Read(ref scanCalls);
        internal string? LastCapabilityScannerId { get; private set; }

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref listCalls);
            return Task.FromResult(new ScannerDiscoveryResult(RegisteredScanners));
        }

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref capabilityCalls);
            LastCapabilityScannerId = scannerId;

            var registered = RegisteredScanners.FirstOrDefault(scanner => scanner.Id == scannerId);
            if (registered is null)
            {
                return Task.FromResult<ScannerDevice?>(null);
            }

            var flatbed = new ScannerSourceCapabilities(
                [300],
                [ScannerColorMode.Grayscale],
                [ScannerPaperSize.A4]);
            return Task.FromResult<ScannerDevice?>(registered with
            {
                SupportsFlatbed = true,
                Capabilities = new ScannerEndpointCapabilities(flatbed, null, null)
            });
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref scanCalls);
            return Task.FromResult<Stream>(new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray(), writable: false));
        }
    }

    private sealed class ThrowingCapabilityAdapter(Exception exception) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ScannerDevice?>(exception);

        public Task<Stream> ScanAsync(string scannerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingScanAdapter(string scannerId, Exception exception) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string requestedScannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScannerDevice?>(CreateCapableScanner(scannerId));

        public Task<Stream> ScanAsync(
            string requestedScannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Stream>(exception);
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(formatter(state, exception), exception));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(formatter(state, exception), exception));
    }

    private sealed record LogEntry(string Message, Exception? Exception);
}
