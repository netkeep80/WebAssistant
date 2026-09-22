using Xunit;

namespace WebAssistant.CoreTests;

public sealed class ScannerCancellationPropagationTests
{
    [Theory]
    [InlineData("WindowsScanAdapter.cs")]
    [InlineData("LinuxScanAdapter.cs")]
    public void Scan_PassesCancellationTokenIntoNaps2SdkOperation(string adapterFileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "webassist",
            "src",
            "WebAssistant",
            "Scanning",
            adapterFileName);
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            ".Scan(options, cancellationToken)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithCancellation(cancellationToken)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Scan(options).WithCancellation(cancellationToken)",
            source,
            StringComparison.Ordinal);
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
