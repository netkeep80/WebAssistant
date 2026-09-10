using System.Diagnostics;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxPublishPlatformPurityTests
{
    [Fact]
    public void LinuxPublish_ContainsNoWindowsNativeRuntimePayload()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "webassist",
            "src",
            "WebAssistant",
            "WebAssistant.csproj");

        using var output = new TemporaryDirectory();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add("linux-x64");
        startInfo.ArgumentList.Add("--self-contained");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(output.Path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"linux-x64 publish failed. stdout: {stdout}\nstderr: {stderr}");

        var files = Directory
            .EnumerateFiles(output.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output.Path, path).Replace('\\', '/'))
            .ToArray();

        Assert.NotEmpty(files);
        Assert.DoesNotContain(
            files,
            path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            files,
            path => path.Contains("runtimes/win-", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "webassist", "WebAssistant.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("WebAssistant repository root was not found.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"webassistant-linux-purity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
