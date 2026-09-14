using System.Globalization;
using System.Text.Json;
using WebAssistant.FileSystem;

namespace WebAssistant.Http;

internal static class FileSystemEndpointHandlers
{
    private const int DefaultListLimit = 200;

    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/filesystem/list", ListAsync);
        api.MapGet("/filesystem/file", DownloadAsync);
        api.MapPut("/filesystem/file", UploadAsync);
        api.MapDelete("/filesystem/file", DeleteFileAsync);
        api.MapPost("/filesystem/directory", CreateDirectoryAsync);
        api.MapDelete("/filesystem/directory", DeleteDirectoryAsync);
        api.MapPost("/filesystem/move", MoveAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var path = QueryValue(request, "path") ?? string.Empty;
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
                FileSystemErrorCodes.InvalidPath,
                StatusCodes.Status400BadRequest,
                "Некорректный limit каталога");
        }

        try
        {
            var page = await fileSystem.ListAsync(
                path,
                limit,
                cursor,
                cancellationToken);
            return Results.Ok(new FileSystemListingResponse(
                path,
                page.Entries.Select(MapEntry).ToArray(),
                page.NextCursor));
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var path = QueryValue(context.Request, "path");
        try
        {
            var stream = await fileSystem.OpenReadAsync(
                path!,
                cancellationToken);
            var fileName = path!
                .Split('/', StringSplitOptions.None)[^1];
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(
                stream,
                "application/octet-stream",
                fileDownloadName: fileName,
                enableRangeProcessing: false);
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var path = QueryValue(request, "path");
        try
        {
            await fileSystem.PublishNewFileAsync(
                path!,
                request.Body,
                cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> DeleteFileAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var path = QueryValue(request, "path");
        try
        {
            await fileSystem.DeleteFileAsync(path!, cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> CreateDirectoryAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var parsed = await ReadJsonAsync<FileSystemDirectoryRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        try
        {
            await fileSystem.CreateDirectoryAsync(
                parsed.Value!.Path!,
                cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> DeleteDirectoryAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var path = QueryValue(request, "path");
        try
        {
            await fileSystem.DeleteEmptyDirectoryAsync(path!, cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> MoveAsync(
        HttpRequest request,
        RootedFileSystemProvider provider,
        CancellationToken cancellationToken)
    {
        var fileSystem = GetFileSystem(provider, out var unavailable);
        if (fileSystem is null)
        {
            return unavailable!;
        }

        var parsed = await ReadJsonAsync<FileSystemMoveRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        try
        {
            await fileSystem.MoveNoReplaceAsync(
                parsed.Value!.SourcePath!,
                parsed.Value.DestinationPath!,
                cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
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

    private static IRootedFileSystem? GetFileSystem(
        RootedFileSystemProvider provider,
        out IResult? unavailable)
    {
        if (provider.FileSystem is not null)
        {
            unavailable = null;
            return provider.FileSystem;
        }

        unavailable = Problem(
            FileSystemErrorCodes.FileSystemUnavailable,
            StatusCodes.Status503ServiceUnavailable,
            "Filesystem capability недоступна");
        return null;
    }

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
                    FileSystemErrorCodes.InvalidPath,
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
                        FileSystemErrorCodes.InvalidPath,
                        StatusCodes.Status400BadRequest,
                        "Пустой JSON filesystem request"))
                : (value, null);
        }
        catch (JsonException)
        {
            return (
                null,
                Problem(
                    FileSystemErrorCodes.InvalidPath,
                    StatusCodes.Status400BadRequest,
                    "Некорректный JSON filesystem request"));
        }
    }

    private static IResult MapError(FileSystemOperationException exception) =>
        exception.Code switch
        {
            FileSystemErrorCodes.InvalidPath => Problem(
                exception.Code,
                StatusCodes.Status400BadRequest,
                "Некорректный filesystem path"),
            FileSystemErrorCodes.NotFound => Problem(
                exception.Code,
                StatusCodes.Status404NotFound,
                "Filesystem object не найден"),
            FileSystemErrorCodes.DestinationExists or
            FileSystemErrorCodes.DirectoryNotEmpty or
            FileSystemErrorCodes.UnsafeLink or
            FileSystemErrorCodes.HardlinkRejected => Problem(
                exception.Code,
                StatusCodes.Status409Conflict,
                "Filesystem operation conflict"),
            FileSystemErrorCodes.BlockedFileType => Problem(
                exception.Code,
                StatusCodes.Status422UnprocessableEntity,
                "Тип файла запрещён"),
            FileSystemErrorCodes.Locked => Problem(
                exception.Code,
                423,
                "Filesystem object заблокирован"),
            FileSystemErrorCodes.FileSystemUnavailable => Problem(
                exception.Code,
                StatusCodes.Status503ServiceUnavailable,
                "Filesystem capability недоступна"),
            _ => Problem(
                FileSystemErrorCodes.FileSystemUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                "Filesystem capability недоступна")
        };

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
