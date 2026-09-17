using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebAssistant.FileSystem;

namespace WebAssistant.Http;

internal static class FileSystemEndpointHandlers
{
    private const int DefaultListLimit = 200;
    private const string LoggerCategory = "WebAssistant.Http.FileSystem";

    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/filesystem/roots", Roots);
        api.MapGet("/filesystem/list", ListAsync);
        api.MapGet("/filesystem/file", DownloadAsync);
        api.MapPut("/filesystem/file", UploadAsync);
        api.MapDelete("/filesystem/file", DeleteFileAsync);
        api.MapPost("/filesystem/directory", CreateDirectoryAsync);
        api.MapDelete("/filesystem/directory", DeleteDirectoryAsync);
        api.MapPost("/filesystem/move", MoveAsync);
    }

    private static IResult Roots(
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            registry.EnsureConfigured();
            loggerFactory.CreateLogger(LoggerCategory).LogInformation(
                "Filesystem operation=roots result=success rootCount={RootCount} elapsedMs={ElapsedMs:F1}",
                registry.RootNames.Count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return Results.Ok(new { roots = registry.RootNames });
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(loggerFactory, "roots", null, exception, started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        var path = QueryValue(request, "path");
        var cursor = QueryValue(request, "cursor");
        var rawLimit = QueryValue(request, "limit");
        var limit = DefaultListLimit;
        if (!string.IsNullOrEmpty(rawLimit) &&
            !int.TryParse(
                rawLimit,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out limit))
        {
            return Problem(
                FileSystemErrorCodes.FileSystemPathInvalid,
                StatusCodes.Status400BadRequest,
                "Некорректный limit каталога");
        }

        try
        {
            resolved = registry.Resolve(path, allowRoot: true);
            var page = await resolved.FileSystem.ListAsync(
                resolved.Path.RelativePath,
                limit,
                UnwrapCursor(resolved.Path.RootName, cursor),
                cancellationToken);
            LogSuccess(loggerFactory, "list", resolved.Path, started);
            return Results.Ok(new FileSystemListingResponse(
                resolved.Path.Value,
                page.Entries.Select(MapEntry).ToArray(),
                WrapCursor(resolved.Path.RootName, page.NextCursor)));
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "list",
                resolved?.Path ?? TryParsePath(path, allowRoot: true),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        var rawPath = QueryValue(context.Request, "path");
        try
        {
            resolved = registry.Resolve(rawPath, allowRoot: false);
            var stream = await resolved.FileSystem.OpenReadAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            var fileName = resolved.Path.RelativePath
                .Split('/', StringSplitOptions.None)[^1];
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            LogSuccess(loggerFactory, "download", resolved.Path, started);
            return Results.File(
                stream,
                "application/octet-stream",
                fileDownloadName: fileName,
                enableRangeProcessing: false);
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "download",
                resolved?.Path ?? TryParsePath(rawPath, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        var rawPath = QueryValue(request, "path");
        try
        {
            resolved = registry.Resolve(rawPath, allowRoot: false);
            await resolved.FileSystem.PublishNewFileAsync(
                resolved.Path.RelativePath,
                request.Body,
                cancellationToken);
            LogSuccess(loggerFactory, "upload", resolved.Path, started);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "upload",
                resolved?.Path ?? TryParsePath(rawPath, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> DeleteFileAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        var rawPath = QueryValue(request, "path");
        try
        {
            resolved = registry.Resolve(rawPath, allowRoot: false);
            await resolved.FileSystem.DeleteFileAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            LogSuccess(loggerFactory, "delete-file", resolved.Path, started);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "delete-file",
                resolved?.Path ?? TryParsePath(rawPath, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> CreateDirectoryAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemDirectoryRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        try
        {
            resolved = registry.Resolve(parsed.Value!.Path, allowRoot: false);
            await resolved.FileSystem.CreateDirectoryAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            LogSuccess(loggerFactory, "create-directory", resolved.Path, started);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "create-directory",
                resolved?.Path ?? TryParsePath(parsed.Value!.Path, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> DeleteDirectoryAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? resolved = null;
        var rawPath = QueryValue(request, "path");
        try
        {
            resolved = registry.Resolve(rawPath, allowRoot: false);
            await resolved.FileSystem.DeleteEmptyDirectoryAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            LogSuccess(loggerFactory, "delete-directory", resolved.Path, started);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "delete-directory",
                resolved?.Path ?? TryParsePath(rawPath, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> MoveAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemMoveRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var started = Stopwatch.GetTimestamp();
        ResolvedFileSystemPath? source = null;
        ResolvedFileSystemPath? destination = null;
        try
        {
            registry.EnsureConfigured();
            var sourcePath = FileSystemLogicalPath.Parse(
                parsed.Value!.SourcePath,
                allowRoot: false);
            var destinationPath = FileSystemLogicalPath.Parse(
                parsed.Value.DestinationPath,
                allowRoot: false);
            if (!string.Equals(
                    sourcePath.RootName,
                    destinationPath.RootName,
                    StringComparison.Ordinal))
            {
                throw new FileSystemOperationException(
                    FileSystemErrorCodes.FileSystemPathInvalid,
                    "Перемещение между логическими корнями запрещено.");
            }

            source = registry.Resolve(sourcePath.Value, allowRoot: false);
            destination = new ResolvedFileSystemPath(
                destinationPath,
                source.FileSystem);
            await source.FileSystem.MoveNoReplaceAsync(
                source.Path.RelativePath,
                destination.Path.RelativePath,
                cancellationToken);
            LogSuccess(loggerFactory, "move", source.Path, started);
            LogSuccess(loggerFactory, "move", destination.Path, started);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "move",
                source?.Path ?? TryParsePath(parsed.Value!.SourcePath, allowRoot: false),
                exception,
                started);
            if (destination?.Path is not null)
            {
                LogFailure(loggerFactory, "move", destination.Path, exception, started);
            }

            return MapError(exception);
        }
    }

    private static FileSystemEntryResponse MapEntry(RootedFileSystemEntry entry) =>
        new(
            entry.Name,
            entry.Kind switch
            {
                RootedEntryKind.File => "file",
                RootedEntryKind.Directory => "directory",
                RootedEntryKind.Link => "link",
                _ => throw new InvalidOperationException("Неизвестный filesystem entry kind.")
            },
            entry.Size,
            entry.CreatedAt,
            entry.LastModifiedAt,
            MapRestrictionCode(entry.RestrictionCode));

    private static string? MapRestrictionCode(string? code) =>
        code switch
        {
            null => null,
            FileSystemErrorCodes.BlockedFileType => "active_extension",
            FileSystemErrorCodes.UnsafeLink => "link",
            FileSystemErrorCodes.HardlinkRejected => "hardlink",
            _ => code
        };

    private static string? QueryValue(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values)
            ? values.ToString()
            : null;

    private static string? WrapCursor(string rootName, string? nativeCursor)
    {
        if (nativeCursor is null)
        {
            return null;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Concat(rootName, "\0", nativeCursor)));
    }

    private static string? UnwrapCursor(string rootName, string? publicCursor)
    {
        if (string.IsNullOrEmpty(publicCursor))
        {
            return null;
        }

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(publicCursor));
            var separator = value.IndexOf('\0');
            if (separator <= 0 ||
                !string.Equals(value[..separator], rootName, StringComparison.Ordinal) ||
                separator == value.Length - 1)
            {
                throw InvalidCursor();
            }

            return value[(separator + 1)..];
        }
        catch (FormatException exception)
        {
            throw new FileSystemOperationException(
                FileSystemErrorCodes.FileSystemPathInvalid,
                "Cursor каталога имеет недопустимый формат.",
                exception);
        }
    }

    private static FileSystemOperationException InvalidCursor() =>
        new(
            FileSystemErrorCodes.FileSystemPathInvalid,
            "Cursor не принадлежит выбранному логическому корню.");

    private static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return (
                null,
                Problem(
                    FileSystemErrorCodes.FileSystemPathInvalid,
                    StatusCodes.Status400BadRequest,
                    "Ожидается JSON filesystem request"));
        }

        try
        {
            var value = await request.ReadFromJsonAsync<T>(
                cancellationToken: cancellationToken);
            return value is null
                ? (
                    null,
                    Problem(
                        FileSystemErrorCodes.FileSystemPathInvalid,
                        StatusCodes.Status400BadRequest,
                        "Пустой JSON filesystem request"))
                : (value, null);
        }
        catch (JsonException)
        {
            return (
                null,
                Problem(
                    FileSystemErrorCodes.FileSystemPathInvalid,
                    StatusCodes.Status400BadRequest,
                    "Некорректный JSON filesystem request"));
        }
    }

    private static FileSystemLogicalPath? TryParsePath(string? value, bool allowRoot)
    {
        try
        {
            return FileSystemLogicalPath.Parse(value, allowRoot);
        }
        catch (FileSystemOperationException)
        {
            return null;
        }
    }

    private static void LogSuccess(
        ILoggerFactory loggerFactory,
        string operation,
        FileSystemLogicalPath path,
        long started)
    {
        loggerFactory.CreateLogger(LoggerCategory).LogInformation(
            "Filesystem operation={Operation} logicalRoot={LogicalRoot} relativePath={RelativePath} result=success errorCode=none elapsedMs={ElapsedMs:F1}",
            operation,
            path.RootName,
            path.RelativePath,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void LogFailure(
        ILoggerFactory loggerFactory,
        string operation,
        FileSystemLogicalPath? path,
        FileSystemOperationException exception,
        long started)
    {
        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var hresult = $"0x{unchecked((uint)exception.HResult):X8}";
        if (path is null)
        {
            logger.LogWarning(
                "Filesystem operation={Operation} result=failed errorCode={ErrorCode} hresult={HResult} elapsedMs={ElapsedMs:F1}",
                operation,
                PublicErrorCode(exception.Code),
                hresult,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return;
        }

        logger.LogWarning(
            "Filesystem operation={Operation} logicalRoot={LogicalRoot} relativePath={RelativePath} result=failed errorCode={ErrorCode} hresult={HResult} elapsedMs={ElapsedMs:F1}",
            operation,
            path.RootName,
            path.RelativePath,
            PublicErrorCode(exception.Code),
            hresult,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static string PublicErrorCode(string code) =>
        code switch
        {
            FileSystemErrorCodes.InvalidPath => FileSystemErrorCodes.FileSystemPathInvalid,
            FileSystemErrorCodes.FileSystemUnavailable => FileSystemErrorCodes.FileSystemRootUnavailable,
            _ => code
        };

    private static IResult MapError(FileSystemOperationException exception)
    {
        var publicCode = PublicErrorCode(exception.Code);
        return publicCode switch
        {
            FileSystemErrorCodes.FileSystemPathInvalid => Problem(
                publicCode,
                StatusCodes.Status400BadRequest,
                "Некорректный логический filesystem path"),
            FileSystemErrorCodes.FileSystemRootNotFound => Problem(
                publicCode,
                StatusCodes.Status404NotFound,
                "Логический корень не найден"),
            FileSystemErrorCodes.NotFound => Problem(
                publicCode,
                StatusCodes.Status404NotFound,
                "Filesystem object не найден"),
            FileSystemErrorCodes.DestinationExists or
            FileSystemErrorCodes.DirectoryNotEmpty or
            FileSystemErrorCodes.UnsafeLink or
            FileSystemErrorCodes.HardlinkRejected => Problem(
                publicCode,
                StatusCodes.Status409Conflict,
                "Filesystem operation conflict"),
            FileSystemErrorCodes.BlockedFileType => Problem(
                publicCode,
                StatusCodes.Status422UnprocessableEntity,
                "Тип файла запрещён"),
            FileSystemErrorCodes.Locked => Problem(
                publicCode,
                423,
                "Filesystem object заблокирован"),
            FileSystemErrorCodes.FileSystemNotConfigured => Problem(
                publicCode,
                StatusCodes.Status503ServiceUnavailable,
                "Файловая подсистема не настроена"),
            FileSystemErrorCodes.FileSystemConfigurationInvalid => Problem(
                publicCode,
                StatusCodes.Status503ServiceUnavailable,
                "Конфигурация файловой подсистемы некорректна"),
            FileSystemErrorCodes.FileSystemRootUnavailable => Problem(
                publicCode,
                StatusCodes.Status503ServiceUnavailable,
                "Логический корень временно недоступен"),
            _ => Problem(
                FileSystemErrorCodes.FileSystemRootUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                "Логический корень временно недоступен")
        };
    }

    private static IResult Problem(
        string code,
        int statusCode,
        string title) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code
            });
}
