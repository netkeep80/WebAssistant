namespace WebAssistant.Http;

internal sealed record FileSystemDirectoryRequest(string? Path);

internal sealed record FileSystemMoveRequest(
    string? SourcePath,
    string? DestinationPath);

internal sealed record FileSystemListingResponse(
    string Path,
    IReadOnlyList<FileSystemEntryResponse> Entries,
    string? NextCursor);

internal sealed record FileSystemEntryResponse(
    string Name,
    string Kind,
    long? Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    string? RestrictionCode);
