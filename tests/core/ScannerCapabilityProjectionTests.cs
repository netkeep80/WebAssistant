using System.Collections.Immutable;
using NAPS2.Images;
using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class ScannerCapabilityProjectionTests
{
    [Fact]
    public void AutoMode_UsesIntersectionOfFlatbedAndFeederCapabilities()
    {
        var flatbed = new ScannerSourceCapabilities(
            [100, 300, 600],
            [ScannerColorMode.Color, ScannerColorMode.Grayscale],
            [ScannerPaperSize.Letter, ScannerPaperSize.A4]);
        var feeder = new ScannerSourceCapabilities(
            [200, 300],
            [ScannerColorMode.Grayscale, ScannerColorMode.BlackAndWhite],
            [ScannerPaperSize.A4]);
        var scanner = new ScannerDevice(
            ScannerIdentity.Create(ScannerBackend.Wia, "projection-test"),
            "Projection test",
            ScannerBackend.Wia,
            SupportsFlatbed: true,
            SupportsFeeder: true,
            SupportsDuplex: false,
            FeederPaperState.Unknown,
            new ScannerEndpointCapabilities(flatbed, feeder, null));

        var auto = Assert.Single(
            ScannerCapabilityProjection.BuildModes(scanner),
            mode => mode.Mode == ScannerRequestMode.Auto);

        Assert.Equal(new[] { 300 }, auto.Settings.DpiValues);
        Assert.Equal(new[] { ScannerColorMode.Grayscale }, auto.Settings.ColorModes);
        Assert.Equal(new[] { ScannerPaperSize.A4 }, auto.Settings.PaperSizes);

        var effective = ScannerCapabilityProjection.ResolveEffectiveSettings(
            scanner,
            ScannerRequestMode.Auto,
            new ScannerRequestedSettings(null, null, null));

        Assert.Equal(300, effective.Dpi);
        Assert.Equal(ScannerColorMode.Grayscale, effective.ColorMode);
        Assert.Equal(ScannerPaperSize.A4, effective.PaperSize);
    }

    [Fact]
    public void Defaults_FallBackDeterministicallyWhenPreferredValuesAreUnavailable()
    {
        var capabilities = new ScannerSourceCapabilities(
            [200, 600],
            [ScannerColorMode.BlackAndWhite, ScannerColorMode.Grayscale],
            [ScannerPaperSize.A5, ScannerPaperSize.A4]);

        Assert.Equal(200, ScannerCapabilityProjection.DefaultDpi(capabilities));
        Assert.Equal(ScannerColorMode.Grayscale, ScannerCapabilityProjection.DefaultColorMode(capabilities));
        Assert.Equal(ScannerPaperSize.A5, ScannerCapabilityProjection.DefaultPaperSize(capabilities));
    }

    [Fact]
    public void Naps2Mapper_NormalizesCapabilitiesAndAppliesEffectiveSettings()
    {
        var caps = new ScanCaps
        {
            FlatbedCaps = new PerSourceCaps
            {
                DpiCaps = new DpiCaps
                {
                    Values = ImmutableList.Create(100, 300, 600)
                },
                BitDepthCaps = new BitDepthCaps
                {
                    SupportsColor = true,
                    SupportsGrayscale = true,
                    SupportsBlackAndWhite = false
                },
                PageSizeCaps = new PageSizeCaps
                {
                    ScanArea = PageSize.A4
                }
            }
        };

        var normalized = Naps2ScannerCapabilityMapper.From(caps);
        Assert.NotNull(normalized.Flatbed);
        Assert.Equal(new[] { 100, 300, 600 }, normalized.Flatbed.DpiValues);
        Assert.Equal(
            new[] { ScannerColorMode.Color, ScannerColorMode.Grayscale },
            normalized.Flatbed.ColorModes);
        Assert.Contains(ScannerPaperSize.A4, normalized.Flatbed.PaperSizes);
        Assert.DoesNotContain(ScannerPaperSize.A3, normalized.Flatbed.PaperSizes);

        var options = new ScanOptions();
        Naps2ScannerCapabilityMapper.Apply(
            options,
            new ScannerEffectiveSettings(
                600,
                ScannerColorMode.Grayscale,
                ScannerPaperSize.A4));

        Assert.Equal(600, options.Dpi);
        Assert.Equal(BitDepth.Grayscale, options.BitDepth);
        Assert.Equal(PageSize.A4, options.PageSize);
    }
}

#pragma warning restore CA2252
