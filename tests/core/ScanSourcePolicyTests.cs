using WebAssistant.Scanning;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScanSourcePolicyTests
{
    [Theory]
    [InlineData(FeederPaperState.Present, ScanSource.Feeder)]
    [InlineData(FeederPaperState.Absent, ScanSource.Glass)]
    [InlineData(FeederPaperState.Unknown, ScanSource.Glass)]
    public void Auto_DualSource_UsesTriStatePaperPolicy(
        FeederPaperState paperState,
        ScanSource expected)
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Auto,
            duplex: false,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            paperState);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(FeederPaperState.Present)]
    [InlineData(FeederPaperState.Absent)]
    [InlineData(FeederPaperState.Unknown)]
    public void Auto_FeederOnly_UsesFeederRegardlessOfPaperState(FeederPaperState paperState)
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Auto,
            duplex: false,
            supportsFlatbed: false,
            supportsFeeder: true,
            supportsDuplex: false,
            paperState);

        Assert.Equal(ScanSource.Feeder, actual);
    }

    [Theory]
    [InlineData(FeederPaperState.Present)]
    [InlineData(FeederPaperState.Absent)]
    [InlineData(FeederPaperState.Unknown)]
    public void Auto_FlatbedOnly_UsesFlatbedRegardlessOfPaperState(FeederPaperState paperState)
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Auto,
            duplex: false,
            supportsFlatbed: true,
            supportsFeeder: false,
            supportsDuplex: false,
            paperState);

        Assert.Equal(ScanSource.Glass, actual);
    }

    [Fact]
    public void Auto_NoUsableSource_FailsBeforeAcquisition()
    {
        Assert.Throws<NotSupportedException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Auto,
            duplex: false,
            supportsFlatbed: false,
            supportsFeeder: false,
            supportsDuplex: false,
            FeederPaperState.Unknown));
    }

    [Fact]
    public void ExplicitFlatbed_NeverFallsBackToFeeder()
    {
        Assert.Throws<NotSupportedException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Flatbed,
            duplex: false,
            supportsFlatbed: false,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present));
    }

    [Fact]
    public void ExplicitFlatbed_IgnoresPaperInFeeder()
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Flatbed,
            duplex: false,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present);

        Assert.Equal(ScanSource.Glass, actual);
    }

    [Theory]
    [InlineData(FeederPaperState.Absent)]
    [InlineData(FeederPaperState.Unknown)]
    public void ExplicitFeeder_NeverFallsBackToFlatbed(FeederPaperState paperState)
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Feeder,
            duplex: false,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: false,
            paperState);

        Assert.Equal(ScanSource.Feeder, actual);
    }

    [Fact]
    public void ExplicitFeeder_RequiresFeederCapability()
    {
        Assert.Throws<NotSupportedException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Feeder,
            duplex: false,
            supportsFlatbed: true,
            supportsFeeder: false,
            supportsDuplex: false,
            FeederPaperState.Present));
    }

    [Fact]
    public void Auto_WithDuplex_IsInvalidBeforeCapabilityChecks()
    {
        Assert.Throws<ArgumentException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Auto,
            duplex: true,
            supportsFlatbed: false,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present));
    }

    [Fact]
    public void Flatbed_WithDuplex_IsInvalidBeforeCapabilityChecks()
    {
        Assert.Throws<ArgumentException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Flatbed,
            duplex: true,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Present));
    }

    [Fact]
    public void Feeder_WithDuplex_UsesConcreteDuplexAcquisitionModeWhenSupported()
    {
        var actual = ScanSourcePolicy.Resolve(
            RequestedScanSource.Feeder,
            duplex: true,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: true,
            FeederPaperState.Unknown);

        Assert.Equal(ScanSource.Duplex, actual);
    }

    [Fact]
    public void Feeder_WithDuplex_RequiresDuplexCapability()
    {
        Assert.Throws<NotSupportedException>(() => ScanSourcePolicy.Resolve(
            RequestedScanSource.Feeder,
            duplex: true,
            supportsFlatbed: true,
            supportsFeeder: true,
            supportsDuplex: false,
            FeederPaperState.Present));
    }
}
