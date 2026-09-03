using System.Reflection;
using NAPS2.Scan;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class LinuxScanAdapterTests
{
    [Fact]
    public void Constructor_OnNonLinux_FailsFast()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() => new LinuxScanAdapter());
    }

    [Theory]
    [InlineData("Glass", "Flatbed")]
    [InlineData("Feeder", "Feeder")]
    [InlineData("Duplex", "Duplex")]
    public void ScanSource_MapsToExactNaps2PaperSource(
        string sourceName,
        string expectedPaperSource)
    {
        var method = typeof(LinuxScanAdapter).GetMethod(
            "MapPaperSource",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var source = Enum.Parse<ScanSource>(sourceName);
        var paperSource = method.Invoke(null, new object?[] { source });

        Assert.NotNull(paperSource);
        Assert.Equal(expectedPaperSource, paperSource.ToString());
    }

    [Theory]
    [InlineData("Feeder")]
    [InlineData("Duplex")]
    public void UnsupportedAdvertisedSource_IsRejectedWithoutFallback(string sourceName)
    {
        var method = typeof(LinuxScanAdapter).GetMethod(
            "EnsureSourceSupported",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var source = Enum.Parse<ScanSource>(sourceName);
        var caps = new PaperSourceCaps
        {
            SupportsFlatbed = true,
            SupportsFeeder = false,
            SupportsDuplex = false
        };

        var error = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, new object?[] { caps, source }));
        var inner = Assert.IsType<InvalidOperationException>(error.InnerException);

        Assert.Contains(sourceName, inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAdapter_UsesDirectSaneSdkWithoutCliOrTempPdfOrchestration()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "webassist",
            "src",
            "WebAssistant",
            "Scanning",
            "LinuxScanAdapter.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("Driver.Sane", source, StringComparison.Ordinal);
        Assert.Contains("ScanController", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Diagnostics", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--source", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Create", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.OpenRead", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "webassist",
                    "src",
                    "WebAssistant",
                    "WebAssistant.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }
}

#pragma warning restore CA2252
