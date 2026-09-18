using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
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
        api.MapGet("/filesystem/files", DownloadZipAsync);
        api.MapGet("/filesystem/find", FindAsync);
        api.MapPost("/filesystem/file", UploadAsync);
        api.MapPost("/filesystem/file/delete", DeleteFileAsync);
        api.MapPost("/filesystem/directory", CreateDirectoryAsync);
        api.MapPost("/filesystem/directory/delete", DeleteDirectoryAsync);
        api.MapPost("/filesystem/move", MoveAsync);
        api.MapPost("/filesystem/directory/move", MoveDirectoryAsync);
        api.MapPost("/filesystem/rename", RenameAsync);
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
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var path = QueryValue(request, "path");
        var wildcard = request.Query.ContainsKey("wildcard")
            ? QueryValue(request, "wildcard")
            : null;
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

        FileSystemLogicalPath? logicalPath = null;
        try
        {
            var page = await service.ListAsync(
                path,
                wildcard,
                limit,
                cursor,
                cancellationToken);
            logicalPath = page.Path;
            LogSuccess(loggerFactory, "list", page.Path, started);
            return Results.Ok(new FileSystemListingResponse(
                page.Path.Value,
                page.Entries.Select(MapEntry).ToArray(),
                page.NextCursor));
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "list",
                logicalPath ?? TryParsePath(path, allowRoot: true),
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

    private static async Task<IResult> DownloadZipAsync(
        HttpRequest request,
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var rawPath = QueryValue(request, "path");
        var wildcard = request.Query.ContainsKey("wildcard")
            ? QueryValue(request, "wildcard")
            : null;
        try
        {
            var selection = await service.SelectZipFilesAsync(
                rawPath,
                wildcard,
                cancellationToken);
            LogSuccess(loggerFactory, "zip", selection.Path, started);
            return new FileSystemZipResult(selection);
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "zip",
                TryParsePath(rawPath, allowRoot: true),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> FindAsync(
        HttpRequest request,
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var rawPath = QueryValue(request, "path");
        var names = request.Query.TryGetValue("name", out var values)
            ? values.ToArray()
            : Array.Empty<string>();
        try
        {
            var found = await service.FindAsync(
                rawPath,
                names,
                cancellationToken);
            var path = TryParsePath(rawPath, allowRoot: true);
            if (path is not null)
            {
                LogSuccess(loggerFactory, "find", path, started);
            }
            return Results.Ok(new FileSystemFileNamesResponse(found));
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "find",
                TryParsePath(rawPath, allowRoot: true),
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
        var parsed = await ReadJsonAsync<FileSystemDirectoryRequest>(request, cancellationToken);
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
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemMoveRequest>(request, cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var moved = await service.MoveFilesAsync(
                parsed.Value!.SourcePath,
                parsed.Value.DestinationPath,
                parsed.Value.FileNames ?? Array.Empty<string>(),
                cancellationToken);
            var source = TryParsePath(parsed.Value.SourcePath, allowRoot: true);
            if (source is not null)
            {
                LogSuccess(loggerFactory, "move-files", source, started);
            }
            return Results.Ok(new FileSystemFileNamesResponse(moved));
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "move-files",
                TryParsePath(parsed.Value!.SourcePath, allowRoot: true),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> MoveDirectoryAsync(
        HttpRequest request,
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemDirectoryMoveRequest>(request, cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await service.MoveDirectoryAsync(
                parsed.Value!.SourcePath,
                parsed.Value.DestinationPath,
                cancellationToken);
            var source = TryParsePath(parsed.Value.SourcePath, allowRoot: false);
            if (source is not null)
            {
                LogSuccess(loggerFactory, "move-directory", source, started);
            }
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "move-directory",
                TryParsePath(parsed.Value!.SourcePath, allowRoot: false),
                exception,
                started);
            return MapError(exception);
        }
    }

    private static async Task<IResult> RenameAsync(
        HttpRequest request,
        FileSystemApplicationService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemRenameRequest>(request, cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await service.RenameAsync(
                parsed.Value!.Path,
                parsed.Value.NewName,
                cancellationToken);
            var path = TryParsePath(parsed.Value.Path, allowRoot: false);
            if (path is not null)
            {
                LogSuccess(loggerFactory, "rename", path, started);
            }
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            LogFailure(
                loggerFactory,
                "rename",
                TryParsePath(parsed.Value!.Path, allowRoot: false),
                exception,
                started);
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

    private sealed class FileSystemZipResult : IResult
    {
        private readonly FileSystemZipSelection selection;

        internal FileSystemZipResult(FileSystemZipSelection selection)
        {
            this.selection = selection;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/zip";
            httpContext.Response.Headers.ContentDisposition = "attachment; filename=files.zip";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";

            try
            {
                using var responseStream =
                    httpContext.Response.BodyWriter.AsStream(leaveOpen: true);
                using (var archive = new ZipArchive(
                    responseStream,
                    ZipArchiveMode.Create,
                    leaveOpen: true))
                {
                    foreach (var fileName in selection.FileNames)
                    {
                        httpContext.RequestAborted.ThrowIfCancellationRequested();
                        await using var source = await selection.FileSystem.OpenReadAsync(
                            FileSystemApplicationService.Join(
                                selection.Path.RelativePath,
                                fileName),
                            httpContext.RequestAborted);
                        var zipEntry = archive.CreateEntry(
                            fileName,
                            CompressionLevel.Fastest);
                        await using var destination = zipEntry.Open();
                        await source.CopyToAsync(
                            destination,
                            64 * 1024,
                            httpContext.RequestAborted);
                    }
                }

                await httpContext.Response.BodyWriter.FlushAsync(
                    httpContext.RequestAborted);
            }
            catch when (httpContext.Response.HasStarted)
            {
                httpContext.Abort();
                throw;
            }
        }
    }
}
