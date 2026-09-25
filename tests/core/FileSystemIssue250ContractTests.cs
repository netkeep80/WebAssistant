using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemIssue250ContractTests : IDisposable
{
    private readonly string tempRoot;
    private readonly string leftRoot;
    private readonly string rightRoot;

    public FileSystemIssue250ContractTests()
    {
        tempRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-issue-250",
            Guid.NewGuid().ToString("N"));
        leftRoot = Path.Combine(tempRoot, "left");
        rightRoot = Path.Combine(tempRoot, "right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);
    }

    [Fact]
    public async Task List_WildcardFiltersFilesAndDirectories_AndStarDotStarIsLiteral()
    {
        Directory.CreateDirectory(Path.Combine(leftRoot, "folder.txt"));
        Directory.CreateDirectory(Path.Combine(leftRoot, "plain-folder"));
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "file.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "plainfile"), "x");

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();

        var txtNames = await ListingNamesAsync(
            client,
            "/v1/filesystem/list?path=archive%2F&wildcard=*.txt");
        Assert.Equal(
            new[] { "file.txt", "folder.txt" },
            txtNames.Order(StringComparer.Ordinal).ToArray());

        var starDotStarNames = await ListingNamesAsync(
            client,
            "/v1/filesystem/list?path=archive%2F&wildcard=*.*");
        Assert.Equal(
            new[] { "file.txt", "folder.txt" },
            starDotStarNames.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task List_OmittedWildcardIsStar_AndPaginationIsOptIn()
    {
        for (var index = 0; index < 225; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(leftRoot, $"f-{index:D3}.bin"),
                index.ToString());
        }

        Directory.CreateDirectory(Path.Combine(leftRoot, "folder"));

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();

        using var omitted = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F");
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        using var omittedJson = JsonDocument.Parse(await omitted.Content.ReadAsStringAsync());
        Assert.Equal(226, omittedJson.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal(
            JsonValueKind.Null,
            omittedJson.RootElement.GetProperty("nextCursor").ValueKind);

        using var explicitStar = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*");
        Assert.Equal(HttpStatusCode.OK, explicitStar.StatusCode);
        using var explicitJson = JsonDocument.Parse(await explicitStar.Content.ReadAsStringAsync());
        Assert.Equal(
            omittedJson.RootElement.GetProperty("entries").EnumerateArray()
                .Select(entry => entry.GetProperty("name").GetString())
                .Order(StringComparer.Ordinal),
            explicitJson.RootElement.GetProperty("entries").EnumerateArray()
                .Select(entry => entry.GetProperty("name").GetString())
                .Order(StringComparer.Ordinal));

        string cursor;
        using (var firstPage = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*&limit=1"))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var firstJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        using var invalid = await client.GetAsync(
            $"/v1/filesystem/list?path=archive%2F&wildcard=*&cursor={Uri.EscapeDataString(cursor)}");
        await AssertProblemCodeAsync(
            invalid,
            HttpStatusCode.BadRequest,
            "filesystem_path_invalid");
    }

    [Fact]
    public async Task Find_IsReadOnlyPostBatch_AndReturnsEntryDtosInInputOrder()
    {
        Directory.CreateDirectory(Path.Combine(leftRoot, "dir1"));
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "a.bin"), "a");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "external.sh"), "restricted");

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();

        using (var response = await client.PostAsJsonAsync(
            "/v1/filesystem/find",
            new
            {
                path = "archive/",
                names = new[] { "dir1", "missing", "external.sh", "a.bin" }
            }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();

            Assert.Equal(
                new[] { "dir1", "external.sh", "a.bin" },
                entries.Select(entry => entry.GetProperty("name").GetString()).ToArray());
            Assert.Equal("directory", entries[0].GetProperty("kind").GetString());
            Assert.Equal("file", entries[1].GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                entries[1].GetProperty("restrictionCode").GetString()));
            Assert.Equal("file", entries[2].GetProperty("kind").GetString());
        }

        using var oldGet = await client.GetAsync(
            "/v1/filesystem/find?path=archive%2F&name=a.bin");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, oldGet.StatusCode);
    }

    [Fact]
    public async Task Find_AcceptsMoreThanOneThousandNames()
    {
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "present.bin"), "x");
        var names = Enumerable.Range(0, 1000)
            .Select(index => $"missing-{index:D4}")
            .Append("present.bin")
            .ToArray();

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/find",
            new { path = "archive/", names });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("present.bin", entry.GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_ExplicitLimitAboveOneThousand_IsAccepted()
    {
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "present.bin"), "x");

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F&wildcard=*&limit=1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("present.bin", entry.GetProperty("name").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Zip_UsesLiteralWildcard_AndDefaultStar()
    {
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "nodot"), "b");

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();

        var literal = await ZipEntryNamesAsync(
            client,
            "/v1/filesystem/files?path=archive%2F&wildcard=*.*");
        Assert.Equal(new[] { "a.txt" }, literal);

        var omitted = await ZipEntryNamesAsync(
            client,
            "/v1/filesystem/files?path=archive%2F");
        Assert.Equal(
            new[] { "a.txt", "nodot" },
            omitted.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Zip_SkipsFileHeldOpenForWriting_AndIncludesReadyFiles()
    {
        var writingPath = Path.Combine(leftRoot, "writing.bin");
        await File.WriteAllTextAsync(writingPath, "in-progress");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "ready.bin"), "ready");

        using var writer = new FileStream(
            writingPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        using var factory = CreateFactory(leftRoot);
        using var client = factory.CreateClient();

        var names = await ZipEntryNamesAsync(
            client,
            "/v1/filesystem/files?path=archive%2F&wildcard=*");

        Assert.Equal(new[] { "ready.bin" }, names);
    }

    [Fact]
    public async Task CrossRootBatchMove_OnSameFilesystem_IsAtomicAndReturnsOnlyMovedNames()
    {
        foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
        {
            await File.WriteAllTextAsync(Path.Combine(leftRoot, name), name);
        }

        await File.WriteAllTextAsync(Path.Combine(rightRoot, "b.bin"), "existing");

        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:left"] = leftRoot,
            ["WebAssistant:FileSystem:right"] = rightRoot
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "left/",
                destinationPath = "right/",
                fileNames = new[] { "a.bin", "b.bin", "missing.bin", "c.bin" }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "a.bin", "c.bin" },
            document.RootElement.GetProperty("fileNames")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());

        Assert.False(File.Exists(Path.Combine(leftRoot, "a.bin")));
        Assert.True(File.Exists(Path.Combine(leftRoot, "b.bin")));
        Assert.False(File.Exists(Path.Combine(leftRoot, "c.bin")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(rightRoot, "b.bin")));
        Assert.True(File.Exists(Path.Combine(rightRoot, "a.bin")));
        Assert.True(File.Exists(Path.Combine(rightRoot, "c.bin")));
    }

    [Fact]
    public async Task CrossRootBatchMove_OverwriteExisting_AtomicallyReplacesFile()
    {
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "replace.bin"), "new");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "replace.bin"), "old");

        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:left"] = leftRoot,
            ["WebAssistant:FileSystem:right"] = rightRoot
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "left/",
                destinationPath = "right/",
                fileNames = new[] { "replace.bin" },
                overwriteExisting = true
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "replace.bin" },
            document.RootElement
                .GetProperty("fileNames")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());

        Assert.False(File.Exists(Path.Combine(leftRoot, "replace.bin")));
        Assert.Equal(
            "new",
            await File.ReadAllTextAsync(Path.Combine(rightRoot, "replace.bin")));
    }

    [Fact]
    public async Task CrossRootDirectoryMove_OnSameFilesystem_PreservesActualBasename()
    {
        var folder = Path.Combine(leftRoot, "folder");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "child.bin"), "child");

        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:left"] = leftRoot,
            ["WebAssistant:FileSystem:right"] = rightRoot
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/directory/move",
            new
            {
                sourcePath = "left/folder",
                destinationPath = "right/"
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(
            "child",
            await File.ReadAllTextAsync(Path.Combine(rightRoot, "folder", "child.bin")));
    }

    [Fact]
    public async Task CrossDeviceMove_FailsExplicitlyWithoutCopyDeleteFallback()
    {
        if (!OperatingSystem.IsLinux() ||
            !Directory.Exists("/dev/shm") ||
            !File.Exists("/proc/self/mountinfo"))
        {
            return;
        }

        var sourceMount = FindLinuxMountPoint(leftRoot);
        var shmMount = FindLinuxMountPoint("/dev/shm");
        if (string.Equals(sourceMount, shmMount, StringComparison.Ordinal))
        {
            return;
        }

        var destination = Path.Combine(
            "/dev/shm",
            "webassistant-issue-250",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(leftRoot, "atomic.bin"), "original");
            using var factory = CreateFactory(new Dictionary<string, string?>
            {
                ["WebAssistant:FileSystem:left"] = leftRoot,
                ["WebAssistant:FileSystem:right"] = destination
            });
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/v1/filesystem/move",
                new
                {
                    sourcePath = "left/",
                    destinationPath = "right/",
                    fileNames = new[] { "atomic.bin" }
                });

            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.Conflict,
                "atomic_move_unavailable");
            Assert.Equal(
                "original",
                await File.ReadAllTextAsync(Path.Combine(leftRoot, "atomic.bin")));
            Assert.False(File.Exists(Path.Combine(destination, "atomic.bin")));
        }
        finally
        {
            try
            {
                Directory.Delete(destination, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void FilesystemRoutes_UseOnlyGetAndPost_AndFindIsPost()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository,
            "webassist",
            "src",
            "WebAssistant",
            "Http",
            "FileSystemEndpointHandlers.cs"));

        Assert.Contains("MapPost(\"/filesystem/find\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet(\"/filesystem/find\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".MapDelete(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".MapPatch(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_ExposesFilesystemLimitsAndRetainedBounds()
    {
        var repository = FindRepositoryRoot();
        var api = File.ReadAllText(Path.Combine(
            repository,
            "webassist",
            "docs",
            "api.md"));

        Assert.Contains(
            "### Ограничения и границы filesystem API",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "| wildcard | Не более 32 comma-separated masks",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "| find batch size | `names` непустой; фиксированного WebAssistant-specific maximum нет.",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "| batch move | `fileNames` = 1..1000 unique single-entry names",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "30,000,000 bytes",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_PanelsOwnRootAndWildcardIndependently()
    {
        var repository = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(
            repository,
            "webassist",
            "src",
            "WebAssistant",
            "wwwroot",
            "filesystem.html"));

        Assert.DoesNotContain("let activeRoot", html, StringComparison.Ordinal);
        Assert.Contains("panels[side].root", html, StringComparison.Ordinal);
        Assert.Contains("wildcard:\"*\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wildcard:\"*.*\"", html, StringComparison.Ordinal);
        Assert.Contains("state.wildcard", html, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(string rootDirectory) =>
        CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = rootDirectory
        });

    private static WebApplicationFactory<Program> CreateFactory(
        IReadOnlyDictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
        });

    private static async Task<string[]> ListingNamesAsync(
        HttpClient client,
        string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static async Task<string[]> ZipEntryNamesAsync(
        HttpClient client,
        string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }

    private static string FindLinuxMountPoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.ReadLines("/proc/self/mountinfo")
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length > 5)
            .Select(parts => parts[4].Replace("\\040", " ", StringComparison.Ordinal))
            .Where(mount =>
                fullPath.Equals(mount, StringComparison.Ordinal) ||
                fullPath.StartsWith(
                    mount.EndsWith('/') ? mount : mount + "/",
                    StringComparison.Ordinal))
            .OrderByDescending(mount => mount.Length)
            .First();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(current.FullName, "contracts")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}
