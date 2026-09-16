using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerRobustnessAndIdentityTests
{
    [Fact]
    public void ScannerIdentity_UsesVersion2AndTruncated96BitSha256()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("wia\0native-42"));
        var expectedToken = Convert.ToBase64String(digest[..12])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var actual = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");

        Assert.Equal(16, expectedToken.Length);
        Assert.Equal("wa2-wia-" + expectedToken, actual);
        Assert.True(ScannerIdentity.TryParse(actual, out var backend));
        Assert.Equal(ScannerBackend.Wia, backend);
        Assert.False(ScannerIdentity.TryParse(
            "wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            out _));
    }

    [Fact]
    public async Task ExplicitSourceScan_DoesNotRequireCapabilityProbe()
    {
        var adapter = new ExplicitScanFakeAdapter();
        using var factory = CreateFactory(adapter);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/scan", new
        {
            scannerId = adapter.ScannerId,
            source = "flatbed",
            settings = new
            {
                duplex = false,
                dpi = 300,
                colorMode = "grayscale",
                paperSize = "a4"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, adapter.ResolveCalls);
        Assert.Equal(0, adapter.CapabilityCalls);
        Assert.Equal(1, adapter.ScanCalls);
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

    private sealed class ExplicitScanFakeAdapter : IScanAdapter
    {
        private int resolveCalls;
        private int capabilityCalls;
        private int scanCalls;

        internal string ScannerId { get; } =
            ScannerIdentity.Create(ScannerBackend.Wia, "explicit-scan-1");

        internal int ResolveCalls => Volatile.Read(ref resolveCalls);
        internal int CapabilityCalls => Volatile.Read(ref capabilityCalls);
        internal int ScanCalls => Volatile.Read(ref scanCalls);

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Global discovery не должен использоваться в этом тесте.");

        public Task<ScannerDevice?> GetScannerAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref resolveCalls);
            return Task.FromResult<ScannerDevice?>(
                scannerId == ScannerId
                    ? new ScannerDevice(
                        ScannerId,
                        "Explicit scan test",
                        ScannerBackend.Wia,
                        SupportsFlatbed: false,
                        SupportsFeeder: false,
                        SupportsDuplex: false,
                        FeederPaperState.Unknown,
                        Capabilities: null,
                        CapabilityState: ScannerCapabilityState.Unavailable)
                    : null);
        }

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref capabilityCalls);
            return Task.FromException<ScannerDevice?>(
                new InvalidOperationException("Capability probe must not be called for explicit source."));
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref scanCalls);
            return Task.FromResult<Stream>(
                new MemoryStream("%PDF-1.7\n%%EOF"u8.ToArray(), writable: false));
        }
    }
}
