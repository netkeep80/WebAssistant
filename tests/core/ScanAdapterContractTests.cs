using System.Text;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScanAdapterContractTests
{
    [Fact]
    public async Task GetScannersAsync_ReturnsConfiguredDevices()
    {
        IScanAdapter adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Основной сканер")],
            "%PDF-1.7\n%%EOF"u8.ToArray());

        var scanners = await adapter.GetScannersAsync();

        var scanner = Assert.Single(scanners);
        Assert.Equal("scanner-1", scanner.Id);
        Assert.Equal("Основной сканер", scanner.Name);
    }

    [Fact]
    public async Task ScanAsync_PreservesScannerIdAndReturnsPdfStream()
    {
        var fake = new FakeScanAdapter(
            [new ScannerDevice("scanner/device:42", "Тестовый сканер")],
            "%PDF-1.7\n%%EOF"u8.ToArray());
        IScanAdapter adapter = fake;

        await using var pdf = await adapter.ScanAsync("scanner/device:42");
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer);

        Assert.Equal("scanner/device:42", fake.LastScannerId);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(buffer.ToArray()));
    }

    [Fact]
    public async Task DefaultSourceOverload_RejectsUnsupportedSourceWithoutFallback()
    {
        IScanAdapter adapter = new FakeScanAdapter(
            [new ScannerDevice("scanner-1", "Сканер")],
            "%PDF-1.7\n%%EOF"u8.ToArray());

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ScanAsync("scanner-1", ScanSource.Feeder));

        Assert.Contains("Feeder", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeScanAdapter(
        IReadOnlyList<ScannerDevice> scanners,
        byte[] pdfBytes) : IScanAdapter
    {
        public string? LastScannerId { get; private set; }

        public Task<IReadOnlyList<ScannerDevice>> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(scanners);
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScannerId = scannerId;
            return Task.FromResult<Stream>(new MemoryStream(pdfBytes, writable: false));
        }
    }
}
