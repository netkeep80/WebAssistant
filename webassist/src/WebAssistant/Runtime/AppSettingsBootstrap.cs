using System.Text;
using Microsoft.AspNetCore.Builder;

namespace WebAssistant.Runtime;

internal sealed class AppSettingsBootstrap : IDisposable
{
    private readonly string? shadowContentRoot;
    private readonly string? originalWebRoot;
    private FileSystemWatcher? watcher;
    private Timer? refreshTimer;
    private readonly object refreshSync = new();
    private bool disposed;

    private AppSettingsBootstrap(
        string? shadowContentRoot,
        string? originalContentRoot,
        string? originalWebRoot)
    {
        this.shadowContentRoot = shadowContentRoot;
        OriginalContentRoot = originalContentRoot;
        this.originalWebRoot = originalWebRoot;
    }

    internal string? OriginalContentRoot { get; }

    internal bool IsPreprocessed => shadowContentRoot is not null;

    internal static AppSettingsBootstrap Prepare(string[] args) =>
        Prepare(args, ResolveContentRoot(args));

    internal static AppSettingsBootstrap Prepare(
        string[] args,
        string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var fullContentRoot = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(fullContentRoot))
        {
            return new AppSettingsBootstrap(
                shadowContentRoot: null,
                originalContentRoot: fullContentRoot,
                originalWebRoot: null);
        }

        var sourceFiles = EnumerateAppSettingsFiles(fullContentRoot);
        if (sourceFiles.Count == 0)
        {
            return new AppSettingsBootstrap(
                shadowContentRoot: null,
                originalContentRoot: fullContentRoot,
                originalWebRoot: ResolveWebRoot(fullContentRoot));
        }

        var normalized = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var requiresShadow = false;

        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile);
            var normalizedText = NativeWindowsPathJsonPreprocessor.Normalize(
                text,
                out var changed);
            normalized[Path.GetFileName(sourceFile)] = normalizedText;
            requiresShadow |= changed;
        }

        if (!requiresShadow)
        {
            return new AppSettingsBootstrap(
                shadowContentRoot: null,
                originalContentRoot: fullContentRoot,
                originalWebRoot: ResolveWebRoot(fullContentRoot));
        }

        var shadowRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-appsettings",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shadowRoot);
        RestrictDirectory(shadowRoot);

        foreach (var pair in normalized)
        {
            WriteShadowFile(
                Path.Combine(shadowRoot, pair.Key),
                pair.Value);
        }

        var bootstrap = new AppSettingsBootstrap(
            shadowRoot,
            fullContentRoot,
            ResolveWebRoot(fullContentRoot));
        bootstrap.StartWatcher();
        return bootstrap;
    }

    internal WebApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (shadowContentRoot is null)
        {
            return WebApplication.CreateBuilder(args);
        }

        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = shadowContentRoot,
            WebRootPath = originalWebRoot
        });
    }

    public void Dispose()
    {
        lock (refreshSync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            refreshTimer?.Dispose();
            refreshTimer = null;
            watcher?.Dispose();
            watcher = null;
        }

        if (shadowContentRoot is not null)
        {
            try
            {
                Directory.Delete(shadowContentRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void StartWatcher()
    {
        if (shadowContentRoot is null ||
            OriginalContentRoot is null ||
            !Directory.Exists(OriginalContentRoot))
        {
            return;
        }

        watcher = new FileSystemWatcher(
            OriginalContentRoot,
            "appsettings*.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnSourceConfigurationChanged;
        watcher.Created += OnSourceConfigurationChanged;
        watcher.Deleted += OnSourceConfigurationChanged;
        watcher.Renamed += OnSourceConfigurationChanged;
    }

    private void OnSourceConfigurationChanged(
        object sender,
        FileSystemEventArgs eventArgs)
    {
        lock (refreshSync)
        {
            if (disposed)
            {
                return;
            }

            refreshTimer ??= new Timer(
                _ => RefreshShadow(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            refreshTimer.Change(
                TimeSpan.FromMilliseconds(150),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void RefreshShadow()
    {
        lock (refreshSync)
        {
            if (disposed ||
                shadowContentRoot is null ||
                OriginalContentRoot is null)
            {
                return;
            }

            try
            {
                var sourceFiles = EnumerateAppSettingsFiles(
                    OriginalContentRoot);
                var sourceNames = new HashSet<string>(
                    sourceFiles.Select(path => Path.GetFileName(path)!),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var sourceFile in sourceFiles)
                {
                    var text = File.ReadAllText(sourceFile);
                    var normalized = NativeWindowsPathJsonPreprocessor.Normalize(
                        text,
                        out _);
                    WriteShadowFileIfChanged(
                        Path.Combine(
                            shadowContentRoot,
                            Path.GetFileName(sourceFile)),
                        normalized);
                }

                foreach (var shadowFile in EnumerateAppSettingsFiles(
                             shadowContentRoot))
                {
                    if (!sourceNames.Contains(Path.GetFileName(shadowFile)))
                    {
                        File.Delete(shadowFile);
                    }
                }
            }
            catch (IOException)
            {
                ScheduleRetry();
            }
            catch (UnauthorizedAccessException)
            {
                ScheduleRetry();
            }
        }
    }

    private void ScheduleRetry()
    {
        if (disposed || refreshTimer is null)
        {
            return;
        }

        refreshTimer.Change(
            TimeSpan.FromMilliseconds(300),
            Timeout.InfiniteTimeSpan);
    }

    private static IReadOnlyList<string> EnumerateAppSettingsFiles(
        string contentRoot) =>
        Directory
            .EnumerateFiles(
                contentRoot,
                "appsettings*.json",
                SearchOption.TopDirectoryOnly)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? ResolveWebRoot(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "wwwroot");
        return Directory.Exists(path)
            ? path
            : null;
    }

    private static string ResolveContentRoot(string[] args)
    {
        var commandLine = ReadContentRootArgument(args);
        if (!string.IsNullOrWhiteSpace(commandLine))
        {
            return Path.GetFullPath(commandLine);
        }

        var dotnet = Environment.GetEnvironmentVariable(
            "DOTNET_CONTENTROOT");
        if (!string.IsNullOrWhiteSpace(dotnet))
        {
            return Path.GetFullPath(dotnet);
        }

        var aspNetCore = Environment.GetEnvironmentVariable(
            "ASPNETCORE_CONTENTROOT");
        if (!string.IsNullOrWhiteSpace(aspNetCore))
        {
            return Path.GetFullPath(aspNetCore);
        }

        var currentDirectory = Environment.CurrentDirectory;
        if (OperatingSystem.IsWindows() &&
            string.Equals(
                currentDirectory,
                Environment.SystemDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return AppContext.BaseDirectory;
        }

        return currentDirectory;
    }

    private static string? ReadContentRootArgument(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument.StartsWith(
                    "--contentRoot=",
                    StringComparison.OrdinalIgnoreCase))
            {
                return argument["--contentRoot=".Length..];
            }

            if (string.Equals(
                    argument,
                    "--contentRoot",
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteShadowFile(
        string path,
        string content)
    {
        File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictFile(path);
    }

    private static void WriteShadowFileIfChanged(
        string path,
        string content)
    {
        if (File.Exists(path) &&
            string.Equals(
                File.ReadAllText(path),
                content,
                StringComparison.Ordinal))
        {
            return;
        }

        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictFile(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
    }
}

internal static class NativeWindowsPathJsonPreprocessor
{
    internal static string Normalize(
        string json,
        out bool changed)
    {
        ArgumentNullException.ThrowIfNull(json);

        changed = false;
        var output = new StringBuilder(json.Length);
        var previousSignificant = '\0';

        for (var index = 0; index < json.Length;)
        {
            if (StartsLineComment(json, index))
            {
                var end = FindLineCommentEnd(json, index + 2);
                output.Append(json, index, end - index);
                index = end;
                continue;
            }

            if (StartsBlockComment(json, index))
            {
                var end = FindBlockCommentEnd(json, index + 2);
                output.Append(json, index, end - index);
                index = end;
                continue;
            }

            var current = json[index];
            if (current == '"')
            {
                var valueString = previousSignificant == ':';
                var pathCandidate =
                    valueString &&
                    LooksLikeWindowsPath(json, index + 1);
                var closingQuote = pathCandidate
                    ? FindPathClosingQuote(json, index + 1)
                    : FindJsonClosingQuote(json, index + 1);

                if (closingQuote < 0)
                {
                    output.Append(json, index, json.Length - index);
                    break;
                }

                var contentStart = index + 1;
                var contentLength = closingQuote - contentStart;
                var content = json.Substring(
                    contentStart,
                    contentLength);

                output.Append('"');
                if (pathCandidate &&
                    NeedsNativePathEncoding(content))
                {
                    output.Append(
                        content.Replace(
                            "\\",
                            "\\\\",
                            StringComparison.Ordinal));
                    changed = true;
                }
                else
                {
                    output.Append(content);
                }

                output.Append('"');
                index = closingQuote + 1;
                previousSignificant = 's';
                continue;
            }

            output.Append(current);
            if (!char.IsWhiteSpace(current))
            {
                previousSignificant = current;
            }

            index++;
        }

        return changed
            ? output.ToString()
            : json;
    }

    private static bool LooksLikeWindowsPath(
        string json,
        int contentStart)
    {
        if (contentStart + 2 < json.Length &&
            char.IsAsciiLetter(json[contentStart]) &&
            json[contentStart + 1] == ':' &&
            json[contentStart + 2] == '\\')
        {
            return true;
        }

        return contentStart + 1 < json.Length &&
               json[contentStart] == '\\' &&
               json[contentStart + 1] == '\\';
    }

    private static bool NeedsNativePathEncoding(
        string content)
    {
        if (content.StartsWith(
                "\\\\",
                StringComparison.Ordinal))
        {
            var firstRun = CountBackslashRun(content, 0);
            if (firstRun == 2)
            {
                return true;
            }
        }

        for (var index = 0; index < content.Length;)
        {
            if (content[index] != '\\')
            {
                index++;
                continue;
            }

            var run = CountBackslashRun(content, index);
            if ((run & 1) != 0)
            {
                return true;
            }

            index += run;
        }

        return false;
    }

    private static int CountBackslashRun(
        string content,
        int start)
    {
        var index = start;
        while (index < content.Length &&
               content[index] == '\\')
        {
            index++;
        }

        return index - start;
    }

    private static int FindJsonClosingQuote(
        string json,
        int start)
    {
        for (var index = start; index < json.Length; index++)
        {
            if (json[index] == '\\')
            {
                index++;
                continue;
            }

            if (json[index] == '"')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindPathClosingQuote(
        string json,
        int start) =>
        json.IndexOf('"', start);

    private static bool StartsLineComment(
        string json,
        int index) =>
        index + 1 < json.Length &&
        json[index] == '/' &&
        json[index + 1] == '/';

    private static bool StartsBlockComment(
        string json,
        int index) =>
        index + 1 < json.Length &&
        json[index] == '/' &&
        json[index + 1] == '*';

    private static int FindLineCommentEnd(
        string json,
        int start)
    {
        var index = start;
        while (index < json.Length &&
               json[index] != '\n')
        {
            index++;
        }

        if (index < json.Length)
        {
            index++;
        }

        return index;
    }

    private static int FindBlockCommentEnd(
        string json,
        int start)
    {
        var index = start;
        while (index + 1 < json.Length)
        {
            if (json[index] == '*' &&
                json[index + 1] == '/')
            {
                return index + 2;
            }

            index++;
        }

        return json.Length;
    }
}
