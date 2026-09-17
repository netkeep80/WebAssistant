using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemApiV230ContractTests : IDisposable
{
    private const string LogicalRoot = "archive";
    private readonly string root;
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public FileSystemApiV230ContractTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-v230-filesystem-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        factory = CreateFactory(root);
        client = factory.CreateClient();
    }

    [Fact]
    public void CandidateV03_DeclaresApproved230SurfaceAndCurrentExecutableEvidence()
    {
        var repository = FindRepositoryRoot();
        var contractPath = Path.Combine(
            repository,
            "contracts",
            "webassistant-contract-v0.3.json");
        var conformancePath = Path.Combine(
            repository,
            "contracts",
            "webassistant-conformance-v0.3.json");
        var contract = JsonNode.Parse(File.ReadAllText(contractPath))!.AsObject();
        var conformance = JsonNode.Parse(File.ReadAllText(conformancePath))!.AsObject();

        Assert.Equal("candidate", contract["status"]?.GetValue<string>());
        Assert.False(contract["accepted"]!.GetValue<bool>());
        Assert.Equal("candidate", conformance["status"]?.GetValue<string>());
        Assert.False(conformance["accepted"]!.GetValue<bool>());

        var fsApi = contract["requirements"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(requirement =>
                requirement["id"]?.GetValue<string>() == "WA-FS-002");
        var statement = fsApi["statement"]!.GetValue<string>();

        foreach (var route in new[]
        {
            "GET /v1/filesystem/roots",
            "GET /v1/filesystem/list",
            "GET /v1/filesystem/file",
            "GET /v1/filesystem/files",
            "GET /v1/filesystem/find",
            "POST /v1/filesystem/file",
            "POST /v1/filesystem/file/delete",
            "POST /v1/filesystem/directory",
            "POST /v1/filesystem/directory/delete",
            "POST /v1/filesystem/move",
            "POST /v1/filesystem/directory/move",
            "POST /v1/filesystem/rename"
        })
        {
            Assert.Contains(route, statement, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("PUT", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", statement, StringComparison.Ordinal);
        Assert.Contains("move", statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сохраняет имя", statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename", statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сохраняет parent", statement, StringComparison.OrdinalIgnoreCase);

        var requiredPaths = conformance["requiredRepositoryPaths"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var requiredPath in new[]
        {
            "webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg",
            "webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg",
            "tests/core/FileSystemApiV230ContractTests.cs",
            "webassist/src/WebAssistant/FileSystem/FileSystemApplicationService.cs",
            "webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs",
            "webassist/src/WebAssistant/wwwroot/filesystem.html"
        })
        {
            Assert.Contains(requiredPath, requiredPaths);
        }

        var vectors = conformance["vectors"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .ToArray();
        var apiVector = vectors.Single(vector =>
            vector["id"]?.GetValue<string>() == "WA-C-FILESYSTEM-API-001");
        var apiAssertion = apiVector["assertion"]!.GetValue<string>();
        foreach (var expected in new[]
        {
            "POST",
            "/v1/filesystem/files",
            "/v1/filesystem/find",
            "wildcard",
            "batch",
            "directory move",
            "rename"
        })
        {
            Assert.Contains(expected, apiAssertion, StringComparison.OrdinalIgnoreCase);
        }

        var apiRequirements = apiVector["requirements"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("WA-FS-010", apiRequirements);
        Assert.Contains("WA-FS-011", apiRequirements);

        var apiEvidence = apiVector["evidence"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var evidencePath in new[]
        {
            "tests/core/FileSystemApiV230ContractTests.cs",
            "tests/core/HttpFileSystemContractTests.cs",
            "webassist/src/WebAssistant/FileSystem/FileSystemApplicationService.cs",
            "webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs"
        })
        {
            Assert.Contains(evidencePath, apiEvidence);
        }

        var browserVector = vectors.Single(vector =>
            vector["id"]?.GetValue<string>() == "WA-C-FILESYSTEM-BROWSER-001");
        var browserAssertion = browserVector["assertion"]!.GetValue<string>();
        foreach (var expected in new[] { "wildcard", "ZIP", "find", "selection", "batch" })
        {
            Assert.Contains(expected, browserAssertion, StringComparison.OrdinalIgnoreCase);
        }
        var browserEvidence = browserVector["evidence"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tests/core/FileSystemPageContractTests.cs", browserEvidence);
        Assert.Contains("tests/core/FileSystemBrowserTests.cs", browserEvidence);
    }

    [Fact]
    public async Task Mutations_UsePostAndMoveRenameAreStructurallySeparated()
    {
        Directory.CreateDirectory(Path.Combine(root, "incoming"));
        Directory.CreateDirectory(Path.Combine(root, "processed"));
        await File.WriteAllTextAsync(Path.Combine(root, "incoming", "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(root, "incoming", "folder"));

        using (var upload = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fincoming%2Fuploaded.bin"))
        {
            upload.Content = new ByteArrayContent("payload"u8.ToArray());
            upload.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(upload);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var rename = await client.PostAsJsonAsync(
            "/v1/filesystem/rename",
            new { path = "archive/incoming/a.txt", newName = "renamed.txt" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        }
        Assert.True(File.Exists(Path.Combine(root, "incoming", "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(root, "processed", "renamed.txt")));

        using (var directoryMove = await client.PostAsJsonAsync(
            "/v1/filesystem/directory/move",
            new
            {
                sourcePath = "archive/incoming/folder",
                destinationPath = "archive/processed/"
            }))
        {
            Assert.Equal(HttpStatusCode.NoContent, directoryMove.StatusCode);
        }
        Assert.True(Directory.Exists(Path.Combine(root, "processed", "folder")));

        using (var deleteFile = await client.PostAsync(
            "/v1/filesystem/file/delete?path=archive%2Fincoming%2Fuploaded.bin",
            content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteFile.StatusCode);
        }

        Directory.CreateDirectory(Path.Combine(root, "empty"));
        using (var deleteDirectory = await client.PostAsync(
            "/v1/filesystem/directory/delete?path=archive%2Fempty",
            content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteDirectory.StatusCode);
        }

        using var oldPut = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=archive%2Flegacy.bin")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        using var oldPutResponse = await client.SendAsync(oldPut);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, oldPutResponse.StatusCode);

        using var oldDeleteResponse = await client.DeleteAsync(
            "/v1/filesystem/file?path=archive%2Fincoming%2Frenamed.txt");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, oldDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task BatchMove_PreservesNamesAndSkipsConflictAndMissingSource()
    {
        Directory.CreateDirectory(Path.Combine(root, "incoming"));
        Directory.CreateDirectory(Path.Combine(root, "processed"));
        await File.WriteAllTextAsync(Path.Combine(root, "incoming", "a.xml"), "a");
        await File.WriteAllTextAsync(Path.Combine(root, "incoming", "b.xml"), "b");
        await File.WriteAllTextAsync(Path.Combine(root, "processed", "b.xml"), "existing");

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "archive/incoming/",
                destinationPath = "archive/processed/",
                fileNames = new[] { "a.xml", "missing.xml", "b.xml" }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "a.xml" },
            document.RootElement
                .GetProperty("fileNames")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.True(File.Exists(Path.Combine(root, "processed", "a.xml")));
        Assert.False(File.Exists(Path.Combine(root, "incoming", "a.xml")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(root, "processed", "b.xml")));
        Assert.True(File.Exists(Path.Combine(root, "incoming", "b.xml")));
    }

    [Fact]
    public async Task ListingWildcard_FiltersFilesBeforePaginationButKeepsDirectories()
    {
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(root, "A.XML"), "a");
        await File.WriteAllTextAsync(Path.Combine(root, "b.json"), "b");
        await File.WriteAllTextAsync(Path.Combine(root, "README"), "readme");
        await File.WriteAllTextAsync(Path.Combine(root, "z.txt"), "z");

        using (var onlyDirectory = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*.nomatch&limit=1"))
        {
            Assert.Equal(HttpStatusCode.OK, onlyDirectory.StatusCode);
            using var document = JsonDocument.Parse(await onlyDirectory.Content.ReadAsStringAsync());
            var entries = document.RootElement
                .GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("name").GetString())
                .ToArray();
            Assert.Equal(new[] { "folder" }, entries);
            Assert.True(
                !document.RootElement.TryGetProperty("nextCursor", out var nextCursor) ||
                nextCursor.ValueKind is JsonValueKind.Null ||
                (nextCursor.ValueKind is JsonValueKind.String &&
                 string.IsNullOrEmpty(nextCursor.GetString())));
        }

        using (var filtered = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*.xml,*.json&limit=200"))
        {
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            using var document = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
            var filteredNames = document.RootElement
                .GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("folder", filteredNames);
            Assert.Contains("A.XML", filteredNames);
            Assert.Contains("b.json", filteredNames);
            Assert.DoesNotContain("README", filteredNames);
            Assert.DoesNotContain("z.txt", filteredNames);
        }

        using var allFiles = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*.*&limit=200");
        Assert.Equal(HttpStatusCode.OK, allFiles.StatusCode);
        using var allDocument = JsonDocument.Parse(await allFiles.Content.ReadAsStringAsync());
        Assert.Contains(
            allDocument.RootElement.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == "README");
    }

    [Fact]
    public async Task FindAndZip_UseCurrentDirectoryOnlyAndPreserveOpaqueBytes()
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "a.xml"), "a");
        await File.WriteAllTextAsync(Path.Combine(root, "B.XML"), "B");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "deep.xml"), "deep");

        using (var find = await client.GetAsync(
            "/v1/filesystem/find?path=archive%2F&name=a.xml&name=b.xml&name=B.XML"))
        {
            Assert.Equal(HttpStatusCode.OK, find.StatusCode);
            using var document = JsonDocument.Parse(await find.Content.ReadAsStringAsync());
            Assert.Equal(
                new[] { "a.xml", "B.XML" },
                document.RootElement
                    .GetProperty("fileNames")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
        }

        using var zip = await client.GetAsync(
            "/v1/filesystem/files?path=archive%2F&wildcard=*.xml");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", zip.Content.Headers.ContentDisposition?.DispositionType);

        await using var zipBytes = await zip.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: false);
        var names = archive.Entries.Select(entry => entry.FullName).Order().ToArray();
        Assert.Equal(new[] { "a.xml", "B.XML" }.Order().ToArray(), names);
        Assert.DoesNotContain("deep.xml", names);
        Assert.All(names, name => Assert.DoesNotContain('/', name));
    }

    [Fact]
    public async Task Zip_PreflightsSelectedFilesBeforeStartingResponse()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path.Combine(root, "unreadable.bin");
        await File.WriteAllTextAsync(path, "opaque");
        var originalMode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            using var response = await client.GetAsync(
                "/v1/filesystem/files?path=archive%2F&wildcard=*.bin");

            Assert.Equal((HttpStatusCode)423, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "locked",
                document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string rootDirectory) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"WebAssistant:FileSystem:{LogicalRoot}"] = rootDirectory
                });
            });
        });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "contracts")) &&
                Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Не найден корень репозитория WebAssistant.");
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
