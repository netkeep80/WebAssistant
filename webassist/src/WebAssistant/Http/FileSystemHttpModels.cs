namespace WebAssistant.Http;

internal sealed record FileSystemDirectoryRequest(string? Path);

internal sealed record FileSystemFindRequest(
    string? Path,
    IReadOnlyList<string>? Names);

internal sealed record FileSystemMoveRequest(
    string? SourcePath,
    string? DestinationPath,
    IReadOnlyList<string>? FileNames);

internal sealed record FileSystemDirectoryMoveRequest(
    string? SourcePath,
    string? DestinationPath);

internal sealed record FileSystemRenameRequest(
    string? Path,
    string? NewName);

internal sealed record FileSystemFileNamesResponse(
    IReadOnlyList<string> FileNames);

internal sealed record FileSystemEntriesResponse(
    IReadOnlyList<FileSystemEntryResponse> Entries);

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
