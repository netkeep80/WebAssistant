using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class LinuxPublishPlatformPurityTests
{
    [Fact]
    public void LinuxPublish_ContainsNoDirectWindowsRuntimePayload()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var output = new TemporaryDirectory();
        var publish = PublishLinux(output.Path);

        Assert.True(
            publish.ExitCode == 0,
            $"linux-x64 publish failed. stdout: {publish.Output}\nstderr: {publish.Error}");

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

        Assert.DoesNotContain(
            files,
            path => path.EndsWith(
                "Microsoft.Extensions.Hosting.WindowsServices.dll",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            files,
            path => path.EndsWith(
                "NAPS2.Sdk.Worker.Win32.dll",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LinuxPublish_StartsAndServesHealthWithoutWindowsRuntimeAssemblies()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var output = new TemporaryDirectory();
        var publish = PublishLinux(output.Path);
        Assert.True(
            publish.ExitCode == 0,
            $"linux-x64 publish failed. stdout: {publish.Output}\nstderr: {publish.Error}");

        var executable = Path.Combine(output.Path, "WebAssistant");
        Assert.True(File.Exists(executable), $"Published executable is missing: {executable}");

        var port = ReserveEphemeralPort();
        var logDirectory = Path.Combine(output.Path, "runtime-logs");
        var dataDirectory = Path.Combine(output.Path, "runtime-data");
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = output.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["WebAssistant__Port"] = port.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["WebAssistant__LogDirectory"] = logDirectory;
        startInfo.Environment["WebAssistant__FileSystem__RootDirectory"] = dataDirectory;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start published WebAssistant.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var healthy = false;
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500)
        };

        for (var attempt = 0; attempt < 50 && !process.HasExited; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/v1/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    healthy = true;
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // Host may still be starting.
            }
            catch (TaskCanceledException)
            {
                // Retry until the bounded startup deadline.
            }

            await Task.Delay(200);
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(
            healthy,
            $"Cleaned linux-x64 publish did not serve /v1/health. exit={process.ExitCode}. stdout: {stdout}\nstderr: {stderr}");
    }

    private static ProcessResult PublishLinux(string outputPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "webassist",
            "src",
            "WebAssistant",
            "WebAssistant.csproj");

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
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
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

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

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
