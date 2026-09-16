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
        Assert.StartsWith("wa2-wia-", first, StringComparison.Ordinal);
        Assert.StartsWith("wa2-twain-", twain, StringComparison.Ordinal);
        Assert.NotEqual(first, twain);
        Assert.Equal(24, first.Length);
    }

    [Fact]
    public void Create_UsesExactBackendZeroSeparatorAndFirst96BitsOfSha256()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("wia\0native-42"));
        var expectedToken = Convert.ToBase64String(digest, 0, 12)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var actual = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");

        Assert.Equal("wa2-wia-" + expectedToken, actual);
        Assert.Equal(16, expectedToken.Length);
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

    [Fact]
    public void ExcludeAmbiguousPublicIds_RemovesEveryEndpointSharingOnePublicId()
    {
        var unique = new ScannerDevice(
            "wa2-wia-AAAAAAAAAAAAAAAA",
            "Unique",
            ScannerBackend.Wia);
        var firstCollision = new ScannerDevice(
            "wa2-wia-BBBBBBBBBBBBBBBB",
            "First collision",
            ScannerBackend.Wia);
        var secondCollision = new ScannerDevice(
            "wa2-wia-BBBBBBBBBBBBBBBB",
            "Second collision",
            ScannerBackend.Wia);

        var result = ScannerIdentity.ExcludeAmbiguousPublicIds(
            [unique, firstCollision, secondCollision],
            out var removedAmbiguousIds);

        Assert.True(removedAmbiguousIds);
        var remaining = Assert.Single(result);
        Assert.Same(unique, remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wa2-wia-")]
    [InlineData("wa2-wia-short")]
    [InlineData("wa2-unknown-AAAAAAAAAAAAAAAA")]
    [InlineData("wa2-wia-AAAAAAAAAAAAAAA")]
    [InlineData("wa2-wia-AAAAAAAAAAAAAAAAA")]
    [InlineData("wa2-wia-AAAAAAAAAAAAAAA=")]
    [InlineData("wa2-wia-AAAAAAAAAAAAAAA+")]
    [InlineData("wa1-wia-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TryParse_RejectsMalformedOrPreviousSchemaIdentity(string value)
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
        var value = $"wa2-{backendToken}-AAAAAAAAAAAAAAAA";

        Assert.True(ScannerIdentity.TryParse(value, out var backend));
        Assert.Equal(expectedBackendName, backend.ToString());
    }
}
