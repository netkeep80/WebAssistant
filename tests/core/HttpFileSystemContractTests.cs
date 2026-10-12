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
    private const string LogicalRoot = "archive";
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
    public async Task FileSystemApi_HappyPathUsesOnlyLogicalPublicSurface()
    {
        foreach (var path in new[] { "archive/incoming", "archive/processed" })
        {
            using var createDirectory = await client.PostAsJsonAsync(
                "/v1/filesystem/directory",
                new { path });
            Assert.Equal(HttpStatusCode.NoContent, createDirectory.StatusCode);
        }

        var payload = "opaque-payload"u8.ToArray();
        using (var upload = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fincoming%2Fa.bin"))
        {
            upload.Content = new ByteArrayContent(payload);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(upload);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var listing = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2Fincoming&limit=200"))
        {
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
            var rootElement = document.RootElement;
            Assert.Equal("archive/incoming", rootElement.GetProperty("path").GetString());
            var entries = rootElement.GetProperty("entries");
            var entry = Assert.Single(entries.EnumerateArray());
            Assert.Equal("a.bin", entry.GetProperty("name").GetString());
            Assert.Equal("file", entry.GetProperty("kind").GetString());
            Assert.Equal(payload.Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(JsonValueKind.Null, rootElement.GetProperty("nextCursor").ValueKind);
        }

        using (var download = await client.GetAsync(
            "/v1/filesystem/file?path=archive%2Fincoming%2Fa.bin"))
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
                sourcePath = "archive/incoming/",
                destinationPath = "archive/processed/",
                fileNames = new[] { "a.bin" }
            }))
        {
            Assert.Equal(HttpStatusCode.OK, move.StatusCode);
            using var document = JsonDocument.Parse(await move.Content.ReadAsStringAsync());
            Assert.Equal(
                new[] { "a.bin" },
                document.RootElement
                    .GetProperty("fileNames")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
        }

        Assert.False(File.Exists(Path.Combine(root, "incoming", "a.bin")));
        Assert.True(File.Exists(Path.Combine(root, "processed", "a.bin")));

        using (var deleteFile = await client.PostAsync(
            "/v1/filesystem/file/delete?path=archive%2Fprocessed%2Fa.bin",
            content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteFile.StatusCode);
        }

        foreach (var path in new[] { "archive/incoming", "archive/processed" })
        {
            using var deleteDirectory = await client.PostAsync(
                $"/v1/filesystem/directory/delete?path={Uri.EscapeDataString(path)}",
                content: null);
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
    public async Task Post_ZeroByteBodyCreatesEmptyFile()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fempty.bin")
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
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fexists.bin")
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
        using (var response = await client.PostAsync(
            "/v1/filesystem/directory/delete?path=archive%2Fnon-empty",
            content: null))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.Conflict,
                "directory_not_empty");
        }

        using (var response = await client.GetAsync(
            "/v1/filesystem/file?path=archive%2Fmissing.bin"))
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
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fpayload.sh")
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
        Directory.CreateDirectory(Path.Combine(root, "processed"));

        using (var listing = await client.GetAsync("/v1/filesystem/list?path=archive%2F"))
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
            "/v1/filesystem/file?path=archive%2Fexternal.sh"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "blocked_file_type");
        }

        using (var move = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "archive/",
                destinationPath = "archive/processed/",
                fileNames = new[] { "external.sh" }
            }))
        {
            await AssertProblemCodeAsync(
                move,
                HttpStatusCode.UnprocessableEntity,
                "blocked_file_type");
        }

        using (var delete = await client.PostAsync(
            "/v1/filesystem/file/delete?path=archive%2Fexternal.sh",
            content: null))
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
            "/v1/filesystem/list?path=archive%2Falpha&limit=2"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal(2, document.RootElement.GetProperty("entries").GetArrayLength());
            cursor = document.RootElement.GetProperty("nextCursor").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        using (var second = await client.GetAsync(
            $"/v1/filesystem/list?path=archive%2Falpha&limit=2&cursor={Uri.EscapeDataString(cursor)}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }

        using (var foreign = await client.GetAsync(
            $"/v1/filesystem/list?path=archive%2Fbeta&limit=2&cursor={Uri.EscapeDataString(cursor)}"))
        {
            await AssertProblemCodeAsync(
                foreign,
                HttpStatusCode.BadRequest,
                "filesystem_path_invalid");
        }
    }

    [Fact]
    public async Task InvalidPath_IsProblemJsonAndNeverDisclosesHostRoot()
    {
        using var response = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F..%2Foutside");

        await AssertProblemCodeAsync(
            response,
            HttpStatusCode.BadRequest,
            "filesystem_path_invalid");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(root, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnavailableRoot_Returns503WithoutBreakingHealth()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-missing-filesystem-root",
            Guid.NewGuid().ToString("N"));
        using var unavailableFactory = CreateFactory(missingRoot);
        using var unavailableClient = unavailableFactory.CreateClient();

        using (var response = await unavailableClient.GetAsync(
            "/v1/filesystem/list?path=archive%2F"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "filesystem_root_unavailable");
        }

        using (var health = await unavailableClient.GetAsync("/v1/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Theory]
    [InlineData("https://untrusted.test")]
    [InlineData("null")]
    [InlineData("http://localhost:9999")]
    [InlineData("http://localhost:17654.attacker.test")]
    [InlineData("http://127.0.0.1:17654/")]
    [InlineData("http://127.0.0.1:17654,https://untrusted.test")]
    public async Task ForeignBrowserOrigin_CannotDeleteFileOrPublishOpaqueUpload(string origin)
    {
        var victim = Path.Combine(root, "victim.txt");
        await File.WriteAllTextAsync(victim, "original");

        using (var delete = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file/delete?path=archive%2Fvictim.txt"))
        {
            delete.Headers.TryAddWithoutValidation("Origin", origin);
            delete.Content = new StringContent("browser=form", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await client.SendAsync(delete);
            await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "filesystem_origin_forbidden");
        }

        Assert.Equal("original", await File.ReadAllTextAsync(victim));

        using (var upload = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file?path=archive%2Fcreated.txt"))
        {
            upload.Headers.TryAddWithoutValidation("Origin", origin);
            upload.Content = new StringContent("attack", System.Text.Encoding.UTF8, "text/plain");
            using var response = await client.SendAsync(upload);
            await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "filesystem_origin_forbidden");
        }

        Assert.False(File.Exists(Path.Combine(root, "created.txt")));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task BrowserFetchMetadataWithoutOrigin_CannotDeleteFile(string fetchSite)
    {
        var victim = Path.Combine(root, "victim.txt");
        await File.WriteAllTextAsync(victim, "original");
        using var delete = new HttpRequestMessage(
            HttpMethod.Post, "/v1/filesystem/file/delete?path=archive%2Fvictim.txt");
        delete.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        using var response = await client.SendAsync(delete);
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "filesystem_origin_forbidden");
        Assert.Equal("original", await File.ReadAllTextAsync(victim));
    }

    [Theory]
    [InlineData("http://127.0.0.1:17654")]
    [InlineData("http://localhost:17654")]
    public async Task DiagnosticPageOwnOrigin_CanDeleteFile(string origin)
    {
        var victim = Path.Combine(root, "victim.txt");
        await File.WriteAllTextAsync(victim, "original");
        using var delete = new HttpRequestMessage(
            HttpMethod.Post, "/v1/filesystem/file/delete?path=archive%2Fvictim.txt");
        delete.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(victim));
    }

    [Fact]
    public async Task ConfiguredCorsOrigin_CanPerformMutationButUnlistedOriginCannot()
    {
        using var allowedFactory = CreateFactory(root, "https://approved.test");
        using var allowedClient = allowedFactory.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Post, "/v1/filesystem/directory");
        allowed.Headers.TryAddWithoutValidation("Origin", "https://approved.test");
        allowed.Content = JsonContent.Create(new { path = "archive/approved" });
        using var allowedResponse = await allowedClient.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(root, "approved")));

        using var denied = new HttpRequestMessage(HttpMethod.Post, "/v1/filesystem/directory");
        denied.Headers.TryAddWithoutValidation("Origin", "https://untrusted.test");
        denied.Content = JsonContent.Create(new { path = "archive/denied" });
        using var deniedResponse = await allowedClient.SendAsync(denied);
        await AssertProblemCodeAsync(deniedResponse, HttpStatusCode.Forbidden, "filesystem_origin_forbidden");
        Assert.False(Directory.Exists(Path.Combine(root, "denied")));
    }

    [Fact]
    public async Task OriginlessProgrammaticPostAndReadOnlyFindRemainCompatible()
    {
        using var create = await client.PostAsJsonAsync(
            "/v1/filesystem/directory", new { path = "archive/safe" });
        Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);

        using var find = new HttpRequestMessage(HttpMethod.Post, "/v1/filesystem/find");
        find.Headers.TryAddWithoutValidation("Origin", "https://untrusted.test");
        find.Content = JsonContent.Create(new { path = "archive/", names = new[] { "safe" } });
        using var response = await client.SendAsync(find);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/filesystem/roots")]
    [InlineData("/v1/filesystem/list?path=archive%2F")]
    [InlineData("/v1/diag/info")]
    [InlineData("/filesystem.html")]
    public async Task HostSpoofing_CannotReadApiDiagnosticsOrStaticPage(string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Host = "attacker.test:17654";

        using var response = await client.SendAsync(request);

        await AssertProblemCodeAsync(
            response, (HttpStatusCode)421, "invalid_local_host");
        Assert.DoesNotContain(
            root, await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("localhost.attacker.test:17654")]
    [InlineData("127.0.0.1.attacker.test:17654")]
    [InlineData("127.0.0.2:17654")]
    [InlineData("localhost:17655")]
    [InlineData("127.0.0.1:17653")]
    public async Task MaliciousOrWrongPortHost_CannotMutateFile(string host)
    {
        var victim = Path.Combine(root, "host-probe.txt");
        await File.WriteAllTextAsync(victim, "original");
        using var delete = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/filesystem/file/delete?path=archive%2Fhost-probe.txt");
        delete.Headers.Host = host;

        using var response = await client.SendAsync(delete);

        await AssertProblemCodeAsync(
            response, (HttpStatusCode)421, "invalid_local_host");
        Assert.Equal("original", await File.ReadAllTextAsync(victim));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost:17654")]
    [InlineData("127.0.0.1:17654")]
    public async Task ExactLocalHost_CanListConfiguredRoots(string host)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/v1/filesystem/roots");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            "archive",
            json.RootElement.GetProperty("roots")
                .EnumerateArray().Select(x => x.GetString()));
    }

    private static WebApplicationFactory<Program> CreateFactory(string rootDirectory, string? corsOrigin = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"WebAssistant:FileSystem:{LogicalRoot}"] = rootDirectory,
                    ["WebAssistant:Cors:Enabled"] = corsOrigin is null ? "false" : "true",
                    ["WebAssistant:Cors:AllowedOrigins:0"] = corsOrigin
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
