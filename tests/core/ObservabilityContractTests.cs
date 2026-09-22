using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NAPS2.Scan;
using WebAssistant.Http;
using WebAssistant.Scanning;
using Xunit;

#pragma warning disable CA2252

namespace WebAssistant.CoreTests;

public sealed class ObservabilityContractTests
{
    private static readonly byte[] DiagnosticPdfBytes =
        "%PDF-1.7\nOBSERVABILITY-DOCUMENT-CONTENT-MARKER\n%%EOF"u8.ToArray();

    [Fact]
    public void RepositoryDefault_ExplicitlyUsesInformationLogging()
    {
        var root = FindRepositoryRoot();
        var json = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "build",
            "common",
            "default-appsettings.json"));

        using var document = JsonDocument.Parse(json);
        var logLevel = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.Equal(
            "Information",
            logLevel.GetProperty("WebAssistant").GetString());
    }

    [Fact]
    public async Task Information_FiltersDebugAndTrace()
    {
        using var fixture = CreateFixture("Information");
        using var client = fixture.Factory.CreateClient();

        var logger = fixture.Factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("WebAssistant.ObservabilityContract");

        logger.LogInformation("observability-info-marker");
        logger.LogDebug("observability-debug-marker");
        logger.LogTrace("observability-trace-marker");

        var log = await ReadTodayLogAsync(fixture.LogDirectory);

        Assert.Contains("observability-info-marker", log, StringComparison.Ordinal);
        Assert.DoesNotContain("observability-debug-marker", log, StringComparison.Ordinal);
        Assert.DoesNotContain("observability-trace-marker", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Debug_IncludesDebugButFiltersTrace()
    {
        using var fixture = CreateFixture("Debug");
        using var client = fixture.Factory.CreateClient();

        var logger = fixture.Factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("WebAssistant.ObservabilityContract");

        logger.LogInformation("observability-info-marker");
        logger.LogDebug("observability-debug-marker");
        logger.LogTrace("observability-trace-marker");

        var log = await ReadTodayLogAsync(fixture.LogDirectory);

        Assert.Contains("observability-info-marker", log, StringComparison.Ordinal);
        Assert.Contains("observability-debug-marker", log, StringComparison.Ordinal);
        Assert.DoesNotContain("observability-trace-marker", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_IncludesTrace()
    {
        using var fixture = CreateFixture("Trace");
        using var client = fixture.Factory.CreateClient();

        var logger = fixture.Factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("WebAssistant.ObservabilityContract");

        logger.LogTrace("observability-trace-marker");

        var log = await ReadTodayLogAsync(fixture.LogDirectory);

        Assert.Contains("observability-trace-marker", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugDiagnostics_IsSelfContainedAndPackageFingerprintIsCached()
    {
        using var fixture = CreateFixture("Debug");
        using var client = fixture.Factory.CreateClient();

        using var firstResponse = await client.GetAsync("/v1/diag/info");
        using var secondResponse = await client.GetAsync("/v1/diag/info");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());

        var firstRoot = first.RootElement;
        var secondRoot = second.RootElement;

        Assert.Equal(
            "Debug",
            firstRoot.GetProperty("diagnosticLevel").GetString());

        var fingerprint = firstRoot.GetProperty("runtimeFingerprint");
        var secondFingerprint = secondRoot.GetProperty("runtimeFingerprint");

        Assert.Equal(
            fingerprint.GetProperty("packageCapturedAtUtc").GetString(),
            secondFingerprint.GetProperty("packageCapturedAtUtc").GetString());

        var process = fingerprint.GetProperty("process");
        Assert.True(process.GetProperty("pid").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(
            process.GetProperty("architecture").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            process.GetProperty("executablePath").GetString()));

        var components = fingerprint.GetProperty("components")
            .EnumerateArray()
            .ToArray();

        Assert.Contains(components, component =>
            component.GetProperty("name").GetString() == "WebAssistant" &&
            component.GetProperty("available").GetBoolean());

        Assert.Contains(components, component =>
            component.GetProperty("name").GetString() == "NAPS2.Sdk" &&
            component.GetProperty("available").GetBoolean());

        Assert.Contains(components, component =>
            component.GetProperty("name").GetString() == "NAPS2.Worker");

        foreach (var component in components.Where(component =>
                     component.GetProperty("available").GetBoolean()))
        {
            Assert.True(component.GetProperty("size").GetInt64() > 0);
            Assert.Matches(
                "^[0-9a-f]{64}$",
                component.GetProperty("sha256").GetString() ?? string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(
                component.GetProperty("filePath").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                component.GetProperty("architecture").GetString()));
        }

        Assert.Equal(JsonValueKind.Array, fingerprint.GetProperty("workers").ValueKind);
    }

    [Fact]
    public async Task InformationDiagnostics_DoesNotExposeDebugFingerprint()
    {
        using var fixture = CreateFixture("Information");
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/diag/info");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            "Information",
            root.GetProperty("diagnosticLevel").GetString());
        Assert.False(root.TryGetProperty("runtimeFingerprint", out _));
        Assert.DoesNotContain("\"sha256\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"filePath\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"executablePath\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScannerHttpAndHandlerLogs_ShareOneOperationId()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Sane,
            "observability-scanner-1");
        var adapter = new FakeScanAdapter(
            [new ScannerDevice(scannerId, "Diagnostic scanner")],
            DiagnosticPdfBytes);

        using var fixture = CreateFixture("Debug", adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/v1/scanners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        var lines = log.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        var handlerLine = Assert.Single(
            lines,
            line => line.Contains(
                "Обнаружено зарегистрированных scanner endpoints",
                StringComparison.Ordinal));
        var httpLine = Assert.Single(
            lines,
            line => line.Contains("GET /v1/scanners", StringComparison.Ordinal) &&
                    line.Contains("status=200", StringComparison.Ordinal));

        var handlerId = ExtractOperationId(handlerLine);
        var httpId = ExtractOperationId(httpLine);

        Assert.False(string.IsNullOrWhiteSpace(handlerId));
        Assert.Equal(handlerId, httpId);
    }

    [Fact]
    public async Task ClientCancellation_IsDistinctFromBackendCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            _ => Task.FromCanceled(cancellation.Token),
            logger);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/scanners";
        context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context));

        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains(
                "cancellation=clientRequested",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(
                "cancellation=backendRequested",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackendCancellationRequest_IsLoggedWhileGetCapsIsStillActive()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Twain,
            "twain-observability-native");
        var logger = new CaptureLogger();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backendCompletion = new TaskCompletionSource<ScanCaps>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = WindowsScanAdapter.ResolveCapabilitiesAsync(
            scannerId,
            driver => Task.FromResult<List<ScanDevice>>(driver switch
            {
                Driver.Twain =>
                [
                    new ScanDevice(
                        Driver.Twain,
                        "twain-observability-native",
                        "TWAIN observability scanner")
                ],
                _ => throw new InvalidOperationException("wrong backend")
            }),
            (_, _) =>
            {
                entered.TrySetResult(true);
                return backendCompletion.Task;
            },
            logger,
            cancellation.Token);

        await entered.Task;
        cancellation.Cancel();

        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Message.Contains("stage=getCaps", StringComparison.Ordinal) &&
                entry.Message.Contains(
                    "cancellation=backendRequested",
                    StringComparison.Ordinal) &&
                entry.Message.Contains(
                    "operationState=active",
                    StringComparison.Ordinal));

        backendCompletion.SetResult(new ScanCaps());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation);
    }

    [Fact]
    public async Task SecondaryLoggingSinkFailure_DoesNotMaskPrimaryScannerFailure()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Twain,
            "logging-failure-scanner");
        var adapter = new FailingCapabilityAdapter(
            new ScannerBackendUnavailableException(
                ScannerBackend.Twain,
                new TimeoutException("PRIMARY-BACKEND-MARKER")));

        using var fixture = CreateFixture(
            "Debug",
            adapter,
            services => services.AddSingleton<ILoggerProvider>(
                new ThrowingLoggerProvider(
                    "WebAssistant.Http.ScannerSettings")));
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v1/scanners/{Uri.EscapeDataString(scannerId)}/settings");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.Contains(
            "Backend выбранного сканера недоступен",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "logging.sink.failure",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "ObjectDisposedException",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PRIMARY-BACKEND-MARKER",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyLog_RoundTripsRussianUtf8ThroughDiagnosticsEndpoint()
    {
        const string marker =
            "UTF8-Проверка: обнаружено, сканирование, ёж, №241";

        using var fixture = CreateFixture("Debug");
        using var client = fixture.Factory.CreateClient();

        var logger = fixture.Factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("WebAssistant.Utf8Contract");
        logger.LogInformation("{Marker}", marker);

        var date = DateOnly.FromDateTime(DateTime.Now);
        using var response = await client.GetAsync(
            $"/v1/diag/logs?date={date:yyyy-MM-dd}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/plain",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "utf-8",
            response.Content.Headers.ContentType?.CharSet);
        Assert.Contains(marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("РћР±", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryOwnedNaps2Instrumentation_DefinesWorkerLeaseAndExitEvidence()
    {
        var root = FindRepositoryRoot();
        var recipe = File.ReadAllText(Path.Combine(
            root,
            "webassist",
            "vendor",
            "naps2",
            "rebuild-fixed-sdk.sh"));

        Assert.Contains("worker.acquire", recipe, StringComparison.Ordinal);
        Assert.Contains("worker.release", recipe, StringComparison.Ordinal);
        Assert.Contains("worker.exit", recipe, StringComparison.Ordinal);
        Assert.Contains("workerPid", recipe, StringComparison.Ordinal);
        Assert.Contains("parentPid", recipe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScannerShutdown_EmitsDistinctStartAndCompletionBoundaries()
    {
        var adapter = new DisposableFakeScanAdapter();
        var holder = new WindowsScanAdapterHolder(() => adapter);
        _ = holder.GetOrCreate();
        var logger = new CaptureLogger<WindowsScannerShutdownHostedService>();
        var service = new WindowsScannerShutdownHostedService(holder, logger);

        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains(
                "scanner.shutdown stage=start",
                StringComparison.Ordinal));
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains(
                "scanner.shutdown stage=complete",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(
                "serviceState=stopped",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TraceScan_NeverWritesPdfPayloadOrBase64()
    {
        var scannerId = ScannerIdentity.Create(
            ScannerBackend.Sane,
            "observability-scanner-2");
        var adapter = new FakeScanAdapter(
            [new ScannerDevice(scannerId, "Diagnostic scanner")],
            DiagnosticPdfBytes);

        using var fixture = CreateFixture("Trace", adapter);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/scan",
            new
            {
                scannerId,
                source = "flatbed",
                settings = new { duplex = false }
            });
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var log = await ReadTodayLogAsync(fixture.LogDirectory);
        Assert.DoesNotContain(
            "OBSERVABILITY-DOCUMENT-CONTENT-MARKER",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(DiagnosticPdfBytes),
            log,
            StringComparison.Ordinal);
    }

    private static string ExtractOperationId(string line)
    {
        var match = Regex.Match(
            line,
            @"(?:^|\s)operationId=(?<id>[^\s]+)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["id"].Value : string.Empty;
    }

    private static async Task<string> ReadTodayLogAsync(string logDirectory)
    {
        var file = Path.Combine(
            logDirectory,
            $"webassistant-{DateTimeOffset.Now:yyyy-MM-dd}.log");

        Assert.True(File.Exists(file), $"Ожидался файл журнала {file}");
        return await File.ReadAllTextAsync(file);
    }

    private static TestFixture CreateFixture(
        string level,
        IScanAdapter? adapter = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-observability-tests",
            Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(testRoot, "logs");
        var fileSystemRoot = Path.Combine(testRoot, "data");
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(fileSystemRoot);

        var settings = new Dictionary<string, string?>
        {
            ["WebAssistant:LogDirectory"] = logDirectory,
            ["WebAssistant:FileSystem:archive"] = fileSystemRoot,
            ["Logging:LogLevel:Default"] = level,
            ["Logging:LogLevel:WebAssistant"] = level
        };

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                if (adapter is not null)
                {
                    services.RemoveAll<IScanAdapter>();
                    services.AddSingleton(adapter);
                }

                configureServices?.Invoke(services);
            });
        });

        return new TestFixture(factory, testRoot, logDirectory);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "repo-policy.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class TestFixture(
        WebApplicationFactory<Program> factory,
        string testRoot,
        string logDirectory) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;

        public string LogDirectory { get; } = logDirectory;

        public void Dispose()
        {
            Factory.Dispose();
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeScanAdapter(
        IReadOnlyList<ScannerDevice> scanners,
        byte[] pdfBytes) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScannerDiscoveryResult(scanners));
        }

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ScannerDevice?>(
                scanners.FirstOrDefault(scanner =>
                    string.Equals(scanner.Id, scannerId, StringComparison.Ordinal)));
        }

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(
                new MemoryStream(pdfBytes, writable: false));
        }
    }

    private sealed class FailingCapabilityAdapter(Exception exception) : IScanAdapter
    {
        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScannerDevice?> GetScannerCapabilitiesAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ScannerDevice?>(exception);

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DisposableFakeScanAdapter : IScanAdapter, IDisposable
    {
        public void Dispose()
        {
        }

        public Task<ScannerDiscoveryResult> GetScannersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannerDiscoveryResult([]));

        public Task<Stream> ScanAsync(
            string scannerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(
                new MemoryStream(DiagnosticPdfBytes, writable: false));
    }

    private sealed class ThrowingLoggerProvider(string throwingCategory)
        : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            string.Equals(
                categoryName,
                throwingCategory,
                StringComparison.Ordinal)
                ? ThrowingLogger.Instance
                : NoopLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        internal static ThrowingLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                throw new ObjectDisposedException("EventLogInternal");
            }
        }
    }

    private sealed class NoopLogger : ILogger
    {
        internal static NoopLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NoopScope : IDisposable
    {
        internal static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

#pragma warning restore CA2252
