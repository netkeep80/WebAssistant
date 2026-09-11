using NAPS2.Images;
using NAPS2.Scan;

#pragma warning disable CA2252

namespace WebAssistant.Scanning;

internal static class Naps2ScannerCapabilityMapper
{
    internal static ScannerEndpointCapabilities From(ScanCaps caps) => new(
        MapSource(caps.FlatbedCaps),
        MapSource(caps.FeederCaps),
        MapSource(caps.DuplexCaps));

    internal static void Apply(ScanOptions options, ScannerEffectiveSettings settings)
    {
        if (settings.Dpi is int dpi)
        {
            options.Dpi = dpi;
        }

        if (settings.ColorMode is ScannerColorMode colorMode)
        {
            options.BitDepth = colorMode switch
            {
                ScannerColorMode.Color => BitDepth.Color,
                ScannerColorMode.Grayscale => BitDepth.Grayscale,
                ScannerColorMode.BlackAndWhite => BitDepth.BlackAndWhite,
                _ => throw new ArgumentOutOfRangeException(nameof(settings), settings, null)
            };
        }

        if (settings.PaperSize is ScannerPaperSize paperSize)
        {
            options.PageSize = paperSize switch
            {
                ScannerPaperSize.Letter => PageSize.Letter,
                ScannerPaperSize.Legal => PageSize.Legal,
                ScannerPaperSize.A5 => PageSize.A5,
                ScannerPaperSize.A4 => PageSize.A4,
                ScannerPaperSize.A3 => PageSize.A3,
                ScannerPaperSize.B5 => PageSize.B5,
                ScannerPaperSize.B4 => PageSize.B4,
                _ => throw new ArgumentOutOfRangeException(nameof(settings), settings, null)
            };
        }
    }

    private static ScannerSourceCapabilities? MapSource(PerSourceCaps? caps)
    {
        if (caps is null)
        {
            return null;
        }

        var dpi = caps.DpiCaps?.Values?
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray() ?? [];

        var colorModes = new List<ScannerColorMode>();
        if (caps.BitDepthCaps?.SupportsColor == true)
        {
            colorModes.Add(ScannerColorMode.Color);
        }
        if (caps.BitDepthCaps?.SupportsGrayscale == true)
        {
            colorModes.Add(ScannerColorMode.Grayscale);
        }
        if (caps.BitDepthCaps?.SupportsBlackAndWhite == true)
        {
            colorModes.Add(ScannerColorMode.BlackAndWhite);
        }

        var paperSizes = new List<ScannerPaperSize>();
        if (caps.PageSizeCaps is not null)
        {
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.Letter, PageSize.Letter, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.Legal, PageSize.Legal, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.A5, PageSize.A5, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.A4, PageSize.A4, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.A3, PageSize.A3, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.B5, PageSize.B5, paperSizes);
            AddIfFits(caps.PageSizeCaps, ScannerPaperSize.B4, PageSize.B4, paperSizes);
        }

        return new ScannerSourceCapabilities(dpi, colorModes, paperSizes);
    }

    private static void AddIfFits(
        PageSizeCaps caps,
        ScannerPaperSize normalized,
        PageSize pageSize,
        ICollection<ScannerPaperSize> target)
    {
        if (caps.Fits(pageSize))
        {
            target.Add(normalized);
        }
    }
}

#pragma warning restore CA2252
