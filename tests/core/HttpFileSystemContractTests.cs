using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class HttpFileSystemContractTests : IDisposable
{
    private readonly string root;
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public HttpFileSystemContractTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "webassistant-http-filesystem-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        factory = CreateFactory(root);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task FileSystemApi_HappyPathUsesOnlyRootRelativePublicSurface()
    {
        using (var createDirectory = await client.PostAsJsonAsync(
            "/v1/filesystem/directory",
            new { path = "incoming" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, createDirectory.StatusCode);
        }

        var payload = "opaque-payload"u8.ToArray();
        using (var upload = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=incoming%2Fa.bin"))
        {
            upload.Content = new ByteArrayContent(payload);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(upload);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var listing = await client.GetAsync(
            "/v1/filesystem/list?path=incoming&limit=200"))
        {
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
            var rootElement = document.RootElement;
            Assert.Equal("incoming", rootElement.GetProperty("path").GetString());
            var entries = rootElement.GetProperty("entries");
            var entry = Assert.Single(entries.EnumerateArray());
            Assert.Equal("a.bin", entry.GetProperty("name").GetString());
            Assert.Equal("file", entry.GetProperty("kind").GetString());
            Assert.Equal(payload.Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(JsonValueKind.Null, rootElement.GetProperty("nextCursor").ValueKind);
        }

        using (var download = await client.GetAsync(
            "/v1/filesystem/file?path=incoming%2Fa.bin"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(
                "nosniff",
                Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
        }

        using (var move = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "incoming/a.bin",
                destinationPath = "incoming/b.bin"
            }))
        {
            Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        }

        using (var deleteFile = await client.DeleteAsync(
            "/v1/filesystem/file?path=incoming%2Fb.bin"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteFile.StatusCode);
        }

        using (var deleteDirectory = await client.DeleteAsync(
            "/v1/filesystem/directory?path=incoming"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteDirectory.StatusCode);
        }

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(root),
            path => !string.Equals(
                Path.GetFileName(path),
                ".webassistant-staging",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Put_ZeroByteBodyCreatesEmptyFile()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=empty.bin")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, new FileInfo(Path.Combine(root, "empty.bin")).Length);
    }

    [Fact]
    public async Task FileSystemApi_NormalizesConflictPolicyErrors()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "exists.bin"), "ORIGINAL");

        using (var upload = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=exists.bin")
        {
            Content = new ByteArrayContent("replacement"u8.ToArray())
        })
        using (var response = await client.SendAsync(upload))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.Conflict,
                "destination_exists");
            Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(Path.Combine(root, "exists.bin")));
        }

        Directory.CreateDirectory(Path.Combine(root, "non-empty"));
        await File.WriteAllTextAsync(Path.Combine(root, "non-empty", "child.txt"), "child");
        using (var response = await client.DeleteAsync(
            "/v1/filesystem/directory?path=non-empty"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.Conflict,
                "directory_not_empty");
        }

        using (var response = await client.GetAsync(
            "/v1/filesystem/file?path=missing.bin"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.NotFound,
                "not_found");
        }
    }

    [Fact]
    public async Task FileSystemApi_BlocksActiveExtensionsAndNeverServesExternalRestrictedFile()
    {
        using (var upload = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=payload.sh")
        {
            Content = new ByteArrayContent("echo unsafe"u8.ToArray())
        })
        using (var response = await client.SendAsync(upload))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "blocked_file_type");
        }

        await File.WriteAllTextAsync(Path.Combine(root, "external.sh"), "echo external");

        using (var listing = await client.GetAsync("/v1/filesystem/list?path="))
        {
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
            var restricted = document.RootElement
                .GetProperty("entries")
                .EnumerateArray()
                .Single(entry => entry.GetProperty("name").GetString() == "external.sh");
            Assert.False(string.IsNullOrWhiteSpace(
                restricted.GetProperty("restrictionCode").GetString()));
        }

        using (var response = await client.GetAsync(
            "/v1/filesystem/file?path=external.sh"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "blocked_file_type");
        }

        using (var move = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new { sourcePath = "external.sh", destinationPath = "external.txt" }))
        {
            await AssertProblemCodeAsync(
                move,
                HttpStatusCode.UnprocessableEntity,
                "blocked_file_type");
        }

        using (var delete = await client.DeleteAsync(
            "/v1/filesystem/file?path=external.sh"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
    }

    [Fact]
    public async Task Listing_PaginatesAndRejectsCursorForAnotherDirectory()
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        await File.WriteAllTextAsync(Path.Combine(root, "alpha", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(root, "alpha", "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(root, "alpha", "c.txt"), "c");

        string cursor;
        using (var first = await client.GetAsync(
            "/v1/filesystem/list?path=alpha&limit=2"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal(2, document.RootElement.GetProperty("entries").GetArrayLength());
            cursor = document.RootElement.GetProperty("nextCursor").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        using (var second = await client.GetAsync(
            $"/v1/filesystem/list?path=alpha&limit=2&cursor={Uri.EscapeDataString(cursor)}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }

        using (var foreign = await client.GetAsync(
            $"/v1/filesystem/list?path=beta&limit=2&cursor={Uri.EscapeDataString(cursor)}"))
        {
            await AssertProblemCodeAsync(
                foreign,
                HttpStatusCode.BadRequest,
                "invalid_path");
        }
    }

    [Fact]
    public async Task InvalidPath_IsProblemJsonAndNeverDisclosesHostRoot()
    {
        using var response = await client.GetAsync(
            "/v1/filesystem/list?path=..%2Foutside");

        await AssertProblemCodeAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid_path");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(root, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnavailableFileSystem_Returns503WithoutBreakingHealth()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-missing-filesystem-root",
            Guid.NewGuid().ToString("N"));
        using var unavailableFactory = CreateFactory(missingRoot);
        using var unavailableClient = unavailableFactory.CreateClient();

        using (var response = await unavailableClient.GetAsync(
            "/v1/filesystem/list?path="))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "filesystem_unavailable");
        }

        using (var health = await unavailableClient.GetAsync("/v1/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string rootDirectory) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebAssistant:FileSystem:RootDirectory"] = rootDirectory
                });
            });
        });

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
