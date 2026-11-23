using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LekhaBook.Api.IntegrationTests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200_Healthy()
    {
        // Don't touch a developer's local database from this test.
        using var client = factory
            .WithWebHostBuilder(b => b.UseSetting("Database:MigrateOnStartup", "false"))
            .CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
