using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class MultiRootHttpContractTests : IDisposable
{
    private readonly string tempRoot;
    private readonly string archiveRoot;
    private readonly string nfsRoot;

    public MultiRootHttpContractTests()
    {
        tempRoot = Path.Combine(
            Path.GetTempPath(),
            "webassistant-multi-root-http-tests",
            Guid.NewGuid().ToString("N"));
        archiveRoot = Path.Combine(tempRoot, "archive");
        nfsRoot = Path.Combine(tempRoot, "nfs");
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(nfsRoot);
    }

    [Fact]
    public async Task Roots_ReturnOnlyConfiguredLogicalNames()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot,
            ["WebAssistant:FileSystem:nfs"] = nfsRoot
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/filesystem/roots");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var roots = document.RootElement.GetProperty("roots")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(new[] { "archive", "nfs" }, roots.Order(StringComparer.Ordinal).ToArray());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(archiveRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nfsRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operations_DispatchByFirstLogicalPathSegment()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot,
            ["WebAssistant:FileSystem:nfs"] = nfsRoot
        });
        using var client = factory.CreateClient();

        using (var create = await client.PostAsJsonAsync(
            "/v1/filesystem/directory",
            new { path = "archive/incoming" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);
        }

        using (var upload = new HttpRequestMessage(
            HttpMethod.Put,
            "/v1/filesystem/file?path=archive%2Fincoming%2Fa.bin")
        {
            Content = new ByteArrayContent("archive-payload"u8.ToArray())
        })
        using (var response = await client.SendAsync(upload))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.True(File.Exists(Path.Combine(archiveRoot, "incoming", "a.bin")));
        Assert.False(File.Exists(Path.Combine(nfsRoot, "incoming", "a.bin")));

        using var listing = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2Fincoming&limit=200");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        Assert.Equal("archive/incoming", document.RootElement.GetProperty("path").GetString());
        Assert.Equal(
            "a.bin",
            Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray())
                .GetProperty("name")
                .GetString());
    }

    [Fact]
    public async Task UnknownRoot_IsDistinctFromInvalidPath()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot
        });
        using var client = factory.CreateClient();

        using (var unknown = await client.GetAsync(
            "/v1/filesystem/list?path=missing%2F"))
        {
            await AssertProblemCodeAsync(
                unknown,
                HttpStatusCode.NotFound,
                "filesystem_root_not_found");
        }

        using (var invalid = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F..%2Foutside"))
        {
            await AssertProblemCodeAsync(
                invalid,
                HttpStatusCode.BadRequest,
                "filesystem_path_invalid");
        }
    }

    [Fact]
    public async Task MissingAndInvalidConfiguration_HaveDistinctStableErrors()
    {
        using (var missingFactory = CreateFactory(new Dictionary<string, string?>()))
        using (var missingClient = missingFactory.CreateClient())
        using (var response = await missingClient.GetAsync("/v1/filesystem/roots"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "filesystem_not_configured");
        }

        using (var invalidFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:bad:name"] = archiveRoot
        }))
        using (var invalidClient = invalidFactory.CreateClient())
        using (var response = await invalidClient.GetAsync("/v1/filesystem/roots"))
        {
            await AssertProblemCodeAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "filesystem_configuration_invalid");
        }
    }

    [Fact]
    public async Task OneUnavailableRoot_DoesNotDisableAnotherRootOrHealth()
    {
        var unavailable = Path.Combine(tempRoot, "offline");
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot,
            ["WebAssistant:FileSystem:offline"] = unavailable
        });
        using var client = factory.CreateClient();

        using (var availableResponse = await client.GetAsync(
            "/v1/filesystem/list?path=archive%2F"))
        {
            Assert.Equal(HttpStatusCode.OK, availableResponse.StatusCode);
        }

        using (var unavailableResponse = await client.GetAsync(
            "/v1/filesystem/list?path=offline%2F"))
        {
            await AssertProblemCodeAsync(
                unavailableResponse,
                HttpStatusCode.ServiceUnavailable,
                "filesystem_root_unavailable");
            var body = await unavailableResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(unavailable, body, StringComparison.OrdinalIgnoreCase);
        }

        using var health = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task CrossRootMove_IsRejectedWithoutMovingOrCopying()
    {
        await File.WriteAllTextAsync(Path.Combine(archiveRoot, "a.txt"), "original");
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot,
            ["WebAssistant:FileSystem:nfs"] = nfsRoot
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "archive/a.txt",
                destinationPath = "nfs/a.txt"
            });

        await AssertProblemCodeAsync(
            response,
            HttpStatusCode.BadRequest,
            "filesystem_path_invalid");
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(archiveRoot, "a.txt")));
        Assert.False(File.Exists(Path.Combine(nfsRoot, "a.txt")));
    }

    [Fact]
    public async Task CrossRootMove_IsRejectedBeforeDestinationAuthorityAcquisition()
    {
        var unavailable = Path.Combine(tempRoot, "offline");
        await File.WriteAllTextAsync(Path.Combine(archiveRoot, "a.txt"), "original");
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["WebAssistant:FileSystem:archive"] = archiveRoot,
            ["WebAssistant:FileSystem:offline"] = unavailable
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/filesystem/move",
            new
            {
                sourcePath = "archive/a.txt",
                destinationPath = "offline/a.txt"
            });

        await AssertProblemCodeAsync(
            response,
            HttpStatusCode.BadRequest,
            "filesystem_path_invalid");
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(archiveRoot, "a.txt")));
        Assert.False(Directory.Exists(unavailable));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IReadOnlyDictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
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
        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}
