using System.Globalization;
using System.Text;
using System.Text.Json;
using WebAssistant.FileSystem;

namespace WebAssistant.Http;

internal static class FileSystemEndpointHandlers
{
    private const int DefaultListLimit = 200;

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

    private static IResult Roots(FileSystemRootRegistry registry)
    {
        try
        {
            registry.EnsureConfigured();
            return Results.Ok(new { roots = registry.RootNames });
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
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
            var resolved = registry.Resolve(path, allowRoot: true);
            var page = await resolved.FileSystem.ListAsync(
                resolved.Path.RelativePath,
                limit,
                UnwrapCursor(resolved.Path.RootName, cursor),
                cancellationToken);
            return Results.Ok(new FileSystemListingResponse(
                resolved.Path.Value,
                page.Entries.Select(MapEntry).ToArray(),
                WrapCursor(resolved.Path.RootName, page.NextCursor)));
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = registry.Resolve(
                QueryValue(context.Request, "path"),
                allowRoot: false);
            var stream = await resolved.FileSystem.OpenReadAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            var fileName = resolved.Path.RelativePath
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
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = registry.Resolve(
                QueryValue(request, "path"),
                allowRoot: false);
            await resolved.FileSystem.PublishNewFileAsync(
                resolved.Path.RelativePath,
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
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = registry.Resolve(
                QueryValue(request, "path"),
                allowRoot: false);
            await resolved.FileSystem.DeleteFileAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> CreateDirectoryAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemDirectoryRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        try
        {
            var resolved = registry.Resolve(parsed.Value!.Path, allowRoot: false);
            await resolved.FileSystem.CreateDirectoryAsync(
                resolved.Path.RelativePath,
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
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = registry.Resolve(
                QueryValue(request, "path"),
                allowRoot: false);
            await resolved.FileSystem.DeleteEmptyDirectoryAsync(
                resolved.Path.RelativePath,
                cancellationToken);
            return Results.NoContent();
        }
        catch (FileSystemOperationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> MoveAsync(
        HttpRequest request,
        FileSystemRootRegistry registry,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadJsonAsync<FileSystemMoveRequest>(
            request,
            cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        try
        {
            var source = registry.Resolve(parsed.Value!.SourcePath, allowRoot: false);
            var destination = registry.Resolve(parsed.Value.DestinationPath, allowRoot: false);
            if (!string.Equals(
                    source.Path.RootName,
                    destination.Path.RootName,
                    StringComparison.Ordinal))
            {
                throw new FileSystemOperationException(
                    FileSystemErrorCodes.FileSystemPathInvalid,
                    "Перемещение между логическими корнями запрещено.");
            }

            await source.FileSystem.MoveNoReplaceAsync(
                source.Path.RelativePath,
                destination.Path.RelativePath,
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

    private static IResult MapError(FileSystemOperationException exception)
    {
        var publicCode = exception.Code switch
        {
            FileSystemErrorCodes.InvalidPath => FileSystemErrorCodes.FileSystemPathInvalid,
            FileSystemErrorCodes.FileSystemUnavailable => FileSystemErrorCodes.FileSystemRootUnavailable,
            _ => exception.Code
        };

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
