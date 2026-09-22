using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebAssistant.FileSystem;
using WebAssistant.Http;
using WebAssistant.Logging;
using WebAssistant.Runtime;
using WebAssistant.Scanning;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var runtimeOptions = WebAssistantRuntimeOptions.Load(context.Configuration);
    options.Listen(runtimeOptions.ListenAddress, runtimeOptions.Port);
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WebAssistant";
});
builder.Services.AddSingleton(serviceProvider =>
    WebAssistantRuntimeOptions.Load(
        serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider =>
    FileSystemRootRegistry.Load(
        serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<FileSystemApplicationService>();
builder.Services.AddSingleton(_ => new AgentRuntimeInfo());
builder.Services.AddSingleton(_ => new RuntimeDiagnosticSnapshotProvider());
builder.Services.AddSingleton(serviceProvider =>
    new DailyLogReader(
        serviceProvider.GetRequiredService<WebAssistantRuntimeOptions>().LogDirectory));
builder.Services.AddSingleton(serviceProvider =>
    new DailyFileLoggerProvider(
        serviceProvider.GetRequiredService<WebAssistantRuntimeOptions>().LogDirectory));
builder.Services.AddSingleton<ILoggerProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<DailyFileLoggerProvider>());
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddCors();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<WindowsScanAdapterHolder>();
    builder.Services.AddSingleton<IScanAdapter>(serviceProvider =>
        serviceProvider.GetRequiredService<WindowsScanAdapterHolder>().GetOrCreate());
    builder.Services.AddHostedService<WindowsScannerShutdownHostedService>();
    builder.Services.Configure<HostOptions>(options =>
        options.ShutdownTimeout = TimeSpan.FromSeconds(20));
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IScanAdapter>(_ => new LinuxScanAdapter());
}

var app = builder.Build();
var runtimeOptions = app.Services.GetRequiredService<WebAssistantRuntimeOptions>();
var runtimeDiagnostics = app.Services.GetRequiredService<RuntimeDiagnosticSnapshotProvider>();
var startupDiagnosticsLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("WebAssistant.Runtime.Diagnostics");
var lifecycleDiagnosticsLogger = new ResilientLogger(
    startupDiagnosticsLogger,
    app.Services
        .GetRequiredService<DailyFileLoggerProvider>()
        .CreateLogger("WebAssistant.Runtime.Diagnostics"));

app.Lifetime.ApplicationStopping.Register(() =>
{
    var workerCount = runtimeDiagnostics.CaptureWorkers().Count;
    lifecycleDiagnosticsLogger.LogInformation(
        "host.shutdown stage=requested observedWorkerCount={WorkerCount}",
        workerCount);
});
app.Lifetime.ApplicationStopped.Register(() =>
    lifecycleDiagnosticsLogger.LogInformation(
        "host.shutdown stage=applicationStopped"));

if (startupDiagnosticsLogger.IsEnabled(LogLevel.Debug))
{
    var snapshot = runtimeDiagnostics.Capture();
    startupDiagnosticsLogger.LogDebug(
        "runtime.fingerprint processPid={ProcessPid} processArchitecture={ProcessArchitecture} packageCapturedAtUtc={PackageCapturedAtUtc}",
        snapshot.Process.Pid,
        snapshot.Process.Architecture,
        snapshot.PackageCapturedAtUtc);

    foreach (var component in snapshot.Components)
    {
        if (!component.Available)
        {
            startupDiagnosticsLogger.LogDebug(
                "runtime.component name={ComponentName} available=false filePath={FilePath}",
                component.Name,
                component.FilePath);
            continue;
        }

        startupDiagnosticsLogger.LogDebug(
            "runtime.component name={ComponentName} available=true filePath={FilePath} fileVersion={FileVersion} productVersion={ProductVersion} assemblyVersion={AssemblyVersion} architecture={Architecture} size={Size} sha256={Sha256}",
            component.Name,
            component.FilePath,
            component.FileVersion,
            component.ProductVersion,
            component.AssemblyVersion,
            component.Architecture,
            component.Size,
            component.Sha256);
    }
}

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

if (runtimeOptions.CorsEnabled)
{
    app.UseCors(policy =>
    {
        policy.SetIsOriginAllowed(runtimeOptions.AllowedOrigins.Contains);
        policy.WithMethods(HttpMethods.Get, HttpMethods.Post);
        policy.WithHeaders("Content-Type");
    });
}

var api = app.MapGroup(ApiVersion.CurrentPrefix);
FileSystemEndpointHandlers.Map(api);

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapGet("/scanners", async (
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    DailyFileLoggerProvider dailyLoggerProvider,
    CancellationToken cancellationToken) =>
{
    const string category = "WebAssistant.Http.Scanners";
    return await ScannerEndpointHandlers.ListAsync(
        services.GetService<IScanAdapter>(),
        new ResilientLogger(
            loggerFactory.CreateLogger(category),
            dailyLoggerProvider.CreateLogger(category)),
        cancellationToken);
});
api.MapGet("/scanner-settings/schema", ScannerSettingsEndpointHandlers.Schema);
api.MapGet("/scanners/{scannerId}/settings", async (
    string scannerId,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    DailyFileLoggerProvider dailyLoggerProvider,
    CancellationToken cancellationToken) =>
{
    const string category = "WebAssistant.Http.ScannerSettings";
    return await ScannerSettingsEndpointHandlers.GetAsync(
        services.GetService<IScanAdapter>(),
        scannerId,
        new ResilientLogger(
            loggerFactory.CreateLogger(category),
            dailyLoggerProvider.CreateLogger(category)),
        cancellationToken);
});
api.MapPost("/scan", async (
    HttpRequest request,
    ScanCoordinator coordinator,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    if (!request.HasJsonContentType())
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Ожидается JSON-запрос сканирования");
    }

    ScanRequest? scanRequest;
    try
    {
        scanRequest = await request.ReadFromJsonAsync<ScanRequest>(
            cancellationToken: cancellationToken);
    }
    catch (JsonException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Некорректный JSON-запрос сканирования");
    }

    return await coordinator.ExecuteAsync(
        services.GetService<IScanAdapter>(),
        scanRequest,
        cancellationToken);
});
api.MapGet("/diag/info", (
    AgentRuntimeInfo runtimeInfo,
    WebAssistantRuntimeOptions options,
    ScanCoordinator coordinator,
    FileSystemRootRegistry fileSystemRegistry,
    RuntimeDiagnosticSnapshotProvider diagnostics,
    ILoggerFactory loggerFactory) =>
{
    var uptime = DateTimeOffset.Now - runtimeInfo.StartedAt;
    var diagnosticLogger = loggerFactory.CreateLogger(
        "WebAssistant.Runtime.Diagnostics");
    var diagnosticLevel = diagnosticLogger.IsEnabled(LogLevel.Trace)
        ? "Trace"
        : diagnosticLogger.IsEnabled(LogLevel.Debug)
            ? "Debug"
            : diagnosticLogger.IsEnabled(LogLevel.Information)
                ? "Information"
                : "Restricted";

    var response = new Dictionary<string, object?>
    {
        ["version"] = runtimeInfo.Version,
        ["os"] = RuntimeInformation.OSDescription,
        ["uptimeSeconds"] = Math.Max(0L, (long)uptime.TotalSeconds),
        ["listenUrl"] = $"http://{options.ListenAddress}:{options.Port}",
        ["apiVersion"] = ApiVersion.Current,
        ["scanState"] = coordinator.IsBusy ? "busy" : "idle",
        ["fileSystemState"] = fileSystemRegistry.DiagnosticState,
        ["diagnosticLevel"] = diagnosticLevel
    };

    if (diagnosticLogger.IsEnabled(LogLevel.Debug))
    {
        response["runtimeFingerprint"] = diagnostics.Capture();
    }

    return Results.Ok(response);
});
api.MapGet("/diag/logs", async (
    string? date,
    DailyLogReader reader,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(date) ||
        !DateOnly.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Некорректная дата журнала");
    }

    var text = await reader.ReadAsync(parsedDate, cancellationToken);
    return text is null
        ? Results.NotFound()
        : Results.Text(text, "text/plain; charset=utf-8");
});

app.Run();

public partial class Program
{
}
