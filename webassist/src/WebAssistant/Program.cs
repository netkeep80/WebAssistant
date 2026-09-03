using System.Globalization;
using System.Runtime.InteropServices;
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
    new RootedPathResolver(
        serviceProvider.GetRequiredService<WebAssistantRuntimeOptions>()
            .FileSystemRootDirectory));
builder.Services.AddSingleton(_ => new AgentRuntimeInfo());
builder.Services.AddSingleton(serviceProvider =>
    new DailyLogReader(
        serviceProvider.GetRequiredService<WebAssistantRuntimeOptions>().LogDirectory));
builder.Services.AddSingleton<ILoggerProvider>(serviceProvider =>
    new DailyFileLoggerProvider(
        serviceProvider.GetRequiredService<WebAssistantRuntimeOptions>().LogDirectory));
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddCors();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IScanAdapter>(_ => new WindowsScanAdapter());
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IScanAdapter>(_ => new LinuxScanAdapter());
}

var app = builder.Build();
var runtimeOptions = app.Services.GetRequiredService<WebAssistantRuntimeOptions>();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

if (runtimeOptions.CorsEnabled)
{
    app.UseCors(policy =>
    {
        policy.SetIsOriginAllowed(runtimeOptions.AllowedOrigins.Contains);
        policy.WithMethods(HttpMethods.Get, HttpMethods.Post);
    });
}

var api = app.MapGroup(ApiVersion.CurrentPrefix);

// Проверка co-change policy; observable HTTP behavior не меняется.
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapGet("/scanners", async (
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    return await ScannerEndpointHandlers.ListAsync(
        services.GetService<IScanAdapter>(),
        loggerFactory.CreateLogger("WebAssistant.Http.Scanners"),
        cancellationToken);
});
api.MapPost("/scan", async (
    string? scannerId,
    ScanCoordinator coordinator,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    return await coordinator.ExecuteAsync(
        services.GetService<IScanAdapter>(),
        scannerId,
        ScanSource.Glass,
        cancellationToken);
});
api.MapPost("/scan/feeder", async (
    string? scannerId,
    ScanCoordinator coordinator,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    return await coordinator.ExecuteAsync(
        services.GetService<IScanAdapter>(),
        scannerId,
        ScanSource.Feeder,
        cancellationToken);
});
api.MapPost("/scan/duplex", async (
    string? scannerId,
    ScanCoordinator coordinator,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    return await coordinator.ExecuteAsync(
        services.GetService<IScanAdapter>(),
        scannerId,
        ScanSource.Duplex,
        cancellationToken);
});
api.MapGet("/diag/info", (
    AgentRuntimeInfo runtimeInfo,
    WebAssistantRuntimeOptions options,
    ScanCoordinator coordinator) =>
{
    var uptime = DateTimeOffset.Now - runtimeInfo.StartedAt;
    return Results.Ok(new
    {
        version = runtimeInfo.Version,
        os = RuntimeInformation.OSDescription,
        uptimeSeconds = Math.Max(0L, (long)uptime.TotalSeconds),
        listenUrl = $"http://{options.ListenAddress}:{options.Port}",
        apiVersion = ApiVersion.Current,
        scanState = coordinator.IsBusy ? "busy" : "idle"
    });
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
