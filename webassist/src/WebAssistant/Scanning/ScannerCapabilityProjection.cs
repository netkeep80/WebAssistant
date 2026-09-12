namespace WebAssistant.Scanning;

internal static class ScannerCapabilityProjection
{
    private static readonly ScannerColorMode[] ColorOrder =
    [
        ScannerColorMode.Color,
        ScannerColorMode.Grayscale,
        ScannerColorMode.BlackAndWhite
    ];

    private static readonly ScannerPaperSize[] PaperOrder =
    [
        ScannerPaperSize.Letter,
        ScannerPaperSize.Legal,
        ScannerPaperSize.A5,
        ScannerPaperSize.A4,
        ScannerPaperSize.A3,
        ScannerPaperSize.B5,
        ScannerPaperSize.B4
    ];

    internal static IReadOnlyList<ScannerModeCapabilities> BuildModes(ScannerDevice scanner)
    {
        var endpoint = scanner.Capabilities ?? ScannerEndpointCapabilities.Empty;
        var modes = new List<ScannerModeCapabilities>();

        if (scanner.SupportsFlatbed || scanner.SupportsFeeder)
        {
            var autoSettings = scanner.SupportsFlatbed && scanner.SupportsFeeder
                ? Intersect(
                    endpoint.Flatbed ?? ScannerSourceCapabilities.Empty,
                    endpoint.Feeder ?? ScannerSourceCapabilities.Empty)
                : scanner.SupportsFeeder
                    ? endpoint.Feeder ?? ScannerSourceCapabilities.Empty
                    : endpoint.Flatbed ?? ScannerSourceCapabilities.Empty;

            modes.Add(new ScannerModeCapabilities(
                ScannerRequestMode.Auto,
                "auto",
                false,
                autoSettings));
        }

        if (scanner.SupportsFlatbed)
        {
            modes.Add(new ScannerModeCapabilities(
                ScannerRequestMode.Flatbed,
                "flatbed",
                false,
                endpoint.Flatbed ?? ScannerSourceCapabilities.Empty));
        }

        if (scanner.SupportsFeeder)
        {
            modes.Add(new ScannerModeCapabilities(
                ScannerRequestMode.Feeder,
                "feeder",
                false,
                endpoint.Feeder ?? ScannerSourceCapabilities.Empty));
        }

        if (scanner.SupportsFeeder && scanner.SupportsDuplex)
        {
            modes.Add(new ScannerModeCapabilities(
                ScannerRequestMode.FeederDuplex,
                "feeder",
                true,
                endpoint.Duplex ?? ScannerSourceCapabilities.Empty));
        }

        return modes;
    }

    internal static ScannerRequestMode ResolveMode(RequestedScanSource source, bool duplex) => source switch
    {
        RequestedScanSource.Auto when !duplex => ScannerRequestMode.Auto,
        RequestedScanSource.Flatbed when !duplex => ScannerRequestMode.Flatbed,
        RequestedScanSource.Feeder when !duplex => ScannerRequestMode.Feeder,
        RequestedScanSource.Feeder when duplex => ScannerRequestMode.FeederDuplex,
        _ => throw new ArgumentException("Некорректное сочетание source и duplex.")
    };

    internal static ScannerEffectiveSettings ResolveEffectiveSettings(
        ScannerDevice scanner,
        ScannerRequestMode mode,
        ScannerRequestedSettings requested)
    {
        var modeCapabilities = BuildModes(scanner).FirstOrDefault(candidate => candidate.Mode == mode)
            ?? throw new NotSupportedException("Запрошенный режим сканирования не поддерживается endpoint.");

        return new ScannerEffectiveSettings(
            ResolveDpi(modeCapabilities.Settings, requested.Dpi),
            ResolveColorMode(modeCapabilities.Settings, requested.ColorMode),
            ResolvePaperSize(modeCapabilities.Settings, requested.PaperSize));
    }

    internal static int? DefaultDpi(ScannerSourceCapabilities capabilities)
    {
        if (capabilities.DpiValues.Count == 0)
        {
            return null;
        }

        return capabilities.DpiValues.Contains(100)
            ? 100
            : capabilities.DpiValues.OrderBy(value => value).First();
    }

    internal static ScannerColorMode? DefaultColorMode(ScannerSourceCapabilities capabilities)
    {
        if (capabilities.ColorModes.Count == 0)
        {
            return null;
        }

        return capabilities.ColorModes.Contains(ScannerColorMode.Color)
            ? ScannerColorMode.Color
            : ColorOrder.First(value => capabilities.ColorModes.Contains(value));
    }

    internal static ScannerPaperSize? DefaultPaperSize(ScannerSourceCapabilities capabilities)
    {
        if (capabilities.PaperSizes.Count == 0)
        {
            return null;
        }

        return capabilities.PaperSizes.Contains(ScannerPaperSize.Letter)
            ? ScannerPaperSize.Letter
            : PaperOrder.First(value => capabilities.PaperSizes.Contains(value));
    }

    internal static string PublicModeName(ScannerRequestMode mode) => mode switch
    {
        ScannerRequestMode.Auto => "auto",
        ScannerRequestMode.Flatbed => "flatbed",
        ScannerRequestMode.Feeder => "feeder",
        ScannerRequestMode.FeederDuplex => "feederDuplex",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static int? ResolveDpi(ScannerSourceCapabilities capabilities, int? requested)
    {
        if (requested is null)
        {
            return DefaultDpi(capabilities);
        }

        if (!capabilities.DpiValues.Contains(requested.Value))
        {
            throw new NotSupportedException($"DPI {requested.Value} не поддерживается выбранным режимом.");
        }

        return requested.Value;
    }

    private static ScannerColorMode? ResolveColorMode(
        ScannerSourceCapabilities capabilities,
        ScannerColorMode? requested)
    {
        if (requested is null)
        {
            return DefaultColorMode(capabilities);
        }

        if (!capabilities.ColorModes.Contains(requested.Value))
        {
            throw new NotSupportedException(
                $"Color mode '{ScannerSettingNames.PublicName(requested.Value)}' не поддерживается выбранным режимом.");
        }

        return requested.Value;
    }

    private static ScannerPaperSize? ResolvePaperSize(
        ScannerSourceCapabilities capabilities,
        ScannerPaperSize? requested)
    {
        if (requested is null)
        {
            return DefaultPaperSize(capabilities);
        }

        if (!capabilities.PaperSizes.Contains(requested.Value))
        {
            throw new NotSupportedException(
                $"Paper size '{ScannerSettingNames.PublicName(requested.Value)}' не поддерживается выбранным режимом.");
        }

        return requested.Value;
    }

    private static ScannerSourceCapabilities Intersect(
        ScannerSourceCapabilities left,
        ScannerSourceCapabilities right)
    {
        var dpi = left.DpiValues
            .Intersect(right.DpiValues)
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        var colors = ColorOrder
            .Where(value => left.ColorModes.Contains(value) && right.ColorModes.Contains(value))
            .ToArray();

        var paperSizes = PaperOrder
            .Where(value => left.PaperSizes.Contains(value) && right.PaperSizes.Contains(value))
            .ToArray();

        return new ScannerSourceCapabilities(dpi, colors, paperSizes);
    }
}
