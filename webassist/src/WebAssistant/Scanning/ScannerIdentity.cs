using System.Security.Cryptography;
using System.Text;

namespace WebAssistant.Scanning;

internal enum ScannerBackend
{
    Wia,
    Twain,
    Sane
}

internal static class ScannerIdentity
{
    private const string Prefix = "wa1-";
    private const int DigestLength = 43;

    internal static string Create(ScannerBackend backend, string nativeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nativeId);

        var token = ToToken(backend);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token + "\0" + nativeId));
        var encoded = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{Prefix}{token}-{encoded}";
    }

    internal static bool TryParse(string? value, out ScannerBackend backend)
    {
        backend = default;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var backendStart = Prefix.Length;
        var separator = value.IndexOf('-', backendStart);
        if (separator < 0)
        {
            return false;
        }

        var backendToken = value[backendStart..separator];
        if (!TryParseBackend(backendToken, out backend))
        {
            return false;
        }

        var digest = value[(separator + 1)..];
        return digest.Length == DigestLength && digest.All(IsBase64UrlCharacter);
    }

    private static string ToToken(ScannerBackend backend) => backend switch
    {
        ScannerBackend.Wia => "wia",
        ScannerBackend.Twain => "twain",
        ScannerBackend.Sane => "sane",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };

    private static bool TryParseBackend(string token, out ScannerBackend backend)
    {
        switch (token)
        {
            case "wia":
                backend = ScannerBackend.Wia;
                return true;
            case "twain":
                backend = ScannerBackend.Twain;
                return true;
            case "sane":
                backend = ScannerBackend.Sane;
                return true;
            default:
                backend = default;
                return false;
        }
    }

    private static bool IsBase64UrlCharacter(char value) =>
        value is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '-' or '_';
}
