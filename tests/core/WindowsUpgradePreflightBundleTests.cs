using System.Xml.Linq;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class WindowsUpgradePreflightBundleTests
{
    [Fact]
    public void BurnChain_RunsVitalPermanentPreflightBeforeMsi()
    {
        var bundlePath = ToFullPath("webassist/build/windows/installer/Bundle.wxs");
        var wix = XDocument.Load(bundlePath);
        var chain = wix.Descendants().Single(element => element.Name.LocalName == "Chain");
        var packages = chain.Elements().ToArray();

        Assert.Equal(2, packages.Length);

        var preflight = packages[0];
        Assert.Equal("ExePackage", preflight.Name.LocalName);
        Assert.Equal("UpgradePreflight", preflight.Attribute("Id")?.Value);
        Assert.Equal("$(var.PreflightPath)", preflight.Attribute("SourceFile")?.Value);
        Assert.Equal("yes", preflight.Attribute("PerMachine")?.Value);
        Assert.Equal("yes", preflight.Attribute("Permanent")?.Value);
        Assert.Equal("yes", preflight.Attribute("Vital")?.Value);
        Assert.NotNull(preflight.Attribute("RepairArguments"));

        var msi = packages[1];
        Assert.Equal("MsiPackage", msi.Name.LocalName);
        Assert.Equal("WebAssistantMsi", msi.Attribute("Id")?.Value);

        var bundle = wix.Descendants().Single(element => element.Name.LocalName == "Bundle");
        Assert.Equal("netkeep80.WebAssistant.Bundle", bundle.Attribute("Id")?.Value);

        var source = File.ReadAllText(bundlePath);
        Assert.DoesNotContain("CloseApplication", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsProducer_PublishesSelfContainedPreflightAndPassesExactPathToBundle()
    {
        var packageScript = ReadRequired("webassist/build/windows/package.ps1");
        var bundleProject = ReadRequired("webassist/build/windows/installer/WebAssistant.Bundle.wixproj");
        var packageSource = ReadRequired("webassist/build/windows/installer/Package.wxs");

        Assert.Contains(
            "build/windows/upgrade-preflight/WebAssistant.UpgradePreflight.csproj",
            packageScript,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--runtime win-x64", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--self-contained true", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PublishSingleFile=true", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PublishTrimmed=true", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p:PreflightPath=$preflightPath", packageScript, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("PreflightPath=$(PreflightPath)", bundleProject, StringComparison.Ordinal);
        Assert.Contains("Id=\"netkeep80.WebAssistant\"", packageSource, StringComparison.Ordinal);
        Assert.Contains("ServiceControl", packageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBuildWorkflows_RequirePreflightProjectInsideCopiedExportRoot()
    {
        var expectedPath = "build/windows/upgrade-preflight/WebAssistant.UpgradePreflight.csproj";
        var buildWorkflow = ReadRequired(".github/workflows/build-installers.yml");
        var serviceWorkflow = ReadRequired(".github/workflows/windows-service.yml");

        Assert.Contains(expectedPath, buildWorkflow, StringComparison.Ordinal);
        Assert.Contains(expectedPath, serviceWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUpgradeAcceptance_UsesExactStagedHistoricalTransport()
    {
        foreach (var workflowPath in new[]
                 {
                     ".github/workflows/windows-service.yml",
                     ".github/workflows/installer-acceptance.yml"
                 })
        {
            var workflow = ReadRequired(workflowPath);

            Assert.Contains("10155509111", workflow, StringComparison.Ordinal);
            Assert.Contains("34485513571", workflow, StringComparison.Ordinal);
            Assert.Contains("run-upgrade-acceptance.ps1", workflow, StringComparison.Ordinal);
        }
    }

    private static string ReadRequired(string relativePath)
    {
        var path = ToFullPath(relativePath);
        Assert.True(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string ToFullPath(string relativePath) =>
        Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
