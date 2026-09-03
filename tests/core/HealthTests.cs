using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class HealthTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/health");
        Assert.True(response.IsSuccessStatusCode);
    }
}
