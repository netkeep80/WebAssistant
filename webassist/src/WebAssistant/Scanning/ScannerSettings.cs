namespace WebAssistant.Scanning;

internal enum ScannerColorMode
{
    Color,
    Grayscale,
    BlackAndWhite
}

internal enum ScannerPaperSize
{
    Letter,
    Legal,
    A5,
    A4,
    A3,
    B5,
    B4
}

internal readonly record struct ScannerRequestedSettings(
    int? Dpi,
    ScannerColorMode? ColorMode,
    ScannerPaperSize? PaperSize);

internal readonly record struct ScannerEffectiveSettings(
    int? Dpi,
    ScannerColorMode? ColorMode,
    ScannerPaperSize? PaperSize)
{
    internal static ScannerEffectiveSettings Unspecified => new(null, null, null);
}

internal static class ScannerSettingNames
{
    internal static bool TryParseColorMode(string? value, out ScannerColorMode? colorMode)
    {
        colorMode = value switch
        {
            null => null,
            "color" => ScannerColorMode.Color,
            "grayscale" => ScannerColorMode.Grayscale,
            "blackAndWhite" => ScannerColorMode.BlackAndWhite,
            _ => null
        };

        return value is null || colorMode.HasValue;
    }

    internal static bool TryParsePaperSize(string? value, out ScannerPaperSize? paperSize)
    {
        paperSize = value switch
        {
            null => null,
            "letter" => ScannerPaperSize.Letter,
            "legal" => ScannerPaperSize.Legal,
            "a5" => ScannerPaperSize.A5,
            "a4" => ScannerPaperSize.A4,
            "a3" => ScannerPaperSize.A3,
            "b5" => ScannerPaperSize.B5,
            "b4" => ScannerPaperSize.B4,
            _ => null
        };

        return value is null || paperSize.HasValue;
    }

    internal static string PublicName(ScannerColorMode value) => value switch
    {
        ScannerColorMode.Color => "color",
        ScannerColorMode.Grayscale => "grayscale",
        ScannerColorMode.BlackAndWhite => "blackAndWhite",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    internal static string PublicName(ScannerPaperSize value) => value switch
    {
        ScannerPaperSize.Letter => "letter",
        ScannerPaperSize.Legal => "legal",
        ScannerPaperSize.A5 => "a5",
        ScannerPaperSize.A4 => "a4",
        ScannerPaperSize.A3 => "a3",
        ScannerPaperSize.B5 => "b5",
        ScannerPaperSize.B4 => "b4",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
