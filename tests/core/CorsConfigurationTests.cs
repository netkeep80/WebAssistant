using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebAssistant.Runtime;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class CorsConfigurationTests
{
    [Fact]
    public async Task Cors_IsDisabledByDefault()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.Add("Origin", "http://localhost:8080");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_WhenEnabled_AllowsOnlyConfiguredExactOrigin()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebAssistant:Cors:Enabled"] = "true",
                    ["WebAssistant:Cors:AllowedOrigins:0"] = "https://example.test"
                });
            });
        });
        using var client = factory.CreateClient();

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        allowedRequest.Headers.Add("Origin", "https://example.test");
        using var allowedResponse = await client.SendAsync(allowedRequest);

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        deniedRequest.Headers.Add("Origin", "https://other.test");
        using var deniedResponse = await client.SendAsync(deniedRequest);

        Assert.Equal(
            "https://example.test",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://example.test/path")]
    [InlineData("https://user@example.test")]
    public void Cors_InvalidOrigin_IsRejected(string origin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAssistant:Cors:Enabled"] = "true",
                ["WebAssistant:Cors:AllowedOrigins:0"] = origin
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            WebAssistantRuntimeOptions.Load(configuration));
    }

    [Fact]
    public void Cors_Disabled_DoesNotRequireOrigins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAssistant:Cors:Enabled"] = "false"
            })
            .Build();

        var options = WebAssistantRuntimeOptions.Load(configuration);

        Assert.False(options.CorsEnabled);
        Assert.Empty(options.AllowedOrigins);
    }
}
