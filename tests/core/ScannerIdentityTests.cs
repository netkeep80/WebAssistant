using System.Security.Cryptography;
using System.Text;
using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerIdentityTests
{
    [Fact]
    public void Create_IsDeterministicAndBackendNamespaced()
    {
        var first = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");
        var second = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");
        var twain = ScannerIdentity.Create(ScannerBackend.Twain, "native-42");

        Assert.Equal(first, second);
        Assert.StartsWith("wa1-wia-", first, StringComparison.Ordinal);
        Assert.StartsWith("wa1-twain-", twain, StringComparison.Ordinal);
        Assert.NotEqual(first, twain);
        Assert.Equal(51, first.Length);
    }

    [Fact]
    public void Create_UsesExactBackendZeroSeparatorAndFullSha256()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("wia\0native-42"));
        var expectedToken = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var actual = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");

        Assert.Equal("wa1-wia-" + expectedToken, actual);
        Assert.Equal(43, expectedToken.Length);
    }

    [Theory]
    [InlineData(" native")]
    [InlineData("native ")]
    [InlineData("Native")]
    public void Create_DoesNotNormalizeNativeIdentity(string altered)
    {
        Assert.NotEqual(
            ScannerIdentity.Create(ScannerBackend.Wia, "native"),
            ScannerIdentity.Create(ScannerBackend.Wia, altered));
    }

    [Fact]
    public void Create_IsIndependentOfEvaluationOrder()
    {
        var inputs = new[]
        {
            (ScannerBackend.Wia, "device-a"),
            (ScannerBackend.Twain, "device-b"),
            (ScannerBackend.Wia, "device-c")
        };

        var forward = inputs.ToDictionary(
            item => (item.Item1, item.Item2),
            item => ScannerIdentity.Create(item.Item1, item.Item2));
        var reverse = inputs.Reverse().ToDictionary(
            item => (item.Item1, item.Item2),
            item => ScannerIdentity.Create(item.Item1, item.Item2));

        Assert.Equal(forward.Count, reverse.Count);
        foreach (var pair in forward)
        {
            Assert.Equal(pair.Value, reverse[pair.Key]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("wa1-wia-")]
    [InlineData("wa1-wia-short")]
    [InlineData("wa1-unknown-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void TryParse_RejectsMalformedIdentity(string value)
    {
        Assert.False(ScannerIdentity.TryParse(value, out _));
    }

    [Theory]
    [InlineData("wia", "Wia")]
    [InlineData("twain", "Twain")]
    [InlineData("sane", "Sane")]
    public void TryParse_AcceptsCanonicalIdentityAndReturnsBackend(
        string backendToken,
        string expectedBackendName)
    {
        var value = $"wa1-{backendToken}-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.True(ScannerIdentity.TryParse(value, out var backend));
        Assert.Equal(expectedBackendName, backend.ToString());
    }
}
