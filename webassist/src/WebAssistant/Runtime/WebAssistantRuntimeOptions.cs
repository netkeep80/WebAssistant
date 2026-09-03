using System.Net;

namespace WebAssistant.Runtime;

internal sealed class WebAssistantRuntimeOptions
{
    // Policy probe: ordinary product-only changes must not require a governance bypass.
    internal const int DefaultPort = 17654;

    private WebAssistantRuntimeOptions(
        int port,
        bool corsEnabled,
        IReadOnlySet<string> allowedOrigins,
        string logDirectory,
        string fileSystemRootDirectory)
    {
        Port = port;
        CorsEnabled = corsEnabled;
        AllowedOrigins = allowedOrigins;
        LogDirectory = logDirectory;
        FileSystemRootDirectory = fileSystemRootDirectory;
    }

    internal int Port { get; }

    internal IPAddress ListenAddress => IPAddress.Loopback;

    internal bool CorsEnabled { get; }

    internal IReadOnlySet<string> AllowedOrigins { get; }

    internal string LogDirectory { get; }

    internal string FileSystemRootDirectory { get; }

    internal static WebAssistantRuntimeOptions Load(IConfiguration configuration)
    {
        RejectAlternativeListenerConfiguration(configuration);

        var port = ParsePort(configuration["WebAssistant:Port"]);
        var corsEnabled = ParseBoolean(
            configuration["WebAssistant:Cors:Enabled"],
            "WebAssistant:Cors:Enabled");
        var allowedOrigins = corsEnabled
            ? ParseAllowedOrigins(
                configuration.GetSection("WebAssistant:Cors:AllowedOrigins"))
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logDirectory = ResolveLogDirectory(
            configuration["WebAssistant:LogDirectory"]);
        var fileSystemRootDirectory = ResolveFileSystemRootDirectory(
            configuration["WebAssistant:FileSystem:RootDirectory"]);

        return new WebAssistantRuntimeOptions(
            port,
            corsEnabled,
            allowedOrigins,
            logDirectory,
            fileSystemRootDirectory);
    }

    private static bool ParseBoolean(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Параметр {key} должен иметь значение true или false.");
        }

        return parsed;
    }

    private static int ParsePort(string? configuredPort)
    {
        if (string.IsNullOrWhiteSpace(configuredPort))
        {
            return DefaultPort;
        }

        if (!int.TryParse(configuredPort, out var port) || port is < 1024 or > 65535)
        {
            throw new InvalidOperationException(
                "Порт WebAssistant должен быть целым числом от 1024 до 65535.");
        }

        return port;
    }

    private static string ResolveLogDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WebAssistant",
                "logs");
        }

        if (OperatingSystem.IsLinux())
        {
            return "/var/log/webassistant";
        }

        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    private static string ResolveFileSystemRootDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WebAssistant",
                "data");
        }

        if (OperatingSystem.IsLinux())
        {
            return "/var/lib/webassistant";
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static IReadOnlySet<string> ParseAllowedOrigins(
        IConfigurationSection section)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in section.GetChildren())
        {
            var origin = child.Value?.Trim();
            if (string.IsNullOrWhiteSpace(origin))
            {
                continue;
            }

            if (origin.Contains('*', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Список разрешённых адресов браузера не может содержать '*'.");
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidOperationException(
                    $"Недопустимый адрес браузера: {origin}");
            }

            var canonicalOrigin = uri.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(
                    origin.TrimEnd('/'),
                    canonicalOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Адрес браузера должен содержать только схему, хост и порт: {origin}");
            }

            origins.Add(canonicalOrigin);
        }

        return origins;
    }

    private static void RejectAlternativeListenerConfiguration(
        IConfiguration configuration)
    {
        var forbiddenKeys = new[] { "urls", "http_ports", "https_ports" };
        foreach (var key in forbiddenKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                throw new InvalidOperationException(
                    "Адрес прослушивания WebAssistant фиксирован на loopback; настраивается только порт WebAssistant.");
            }
        }

        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "Произвольные конечные точки Kestrel для WebAssistant запрещены.");
        }
    }
}
