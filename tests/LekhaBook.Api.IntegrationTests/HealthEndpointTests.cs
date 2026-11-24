using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Runs without a database: the connection string is a placeholder that is never opened.</summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200_Healthy()
    {
        using var client = factory
            .WithTestSettings(new()
            {
                ["ConnectionStrings:LekhaBook"] = "Host=localhost;Port=1;Database=unused;Username=unused",
                ["Database:MigrateOnStartup"] = "false",
            })
            .CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
