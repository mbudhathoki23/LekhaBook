using System.Net.Http.Headers;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>
/// The real API on a fresh PostgreSQL database created for this test class and dropped afterwards.
/// The connection string is overridden explicitly, so a developer's own database is never used.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private string? _databaseName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        if (TestPostgres.ServerConnectionString is null)
        {
            return Task.CompletedTask; // tests are skipped
        }

        var connectionString = TestPostgres.NewDatabase(out var name);
        _databaseName = name;
        Factory = new WebApplicationFactory<Program>().WithTestSettings(new()
        {
            ["ConnectionStrings:LekhaBook"] = connectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["RateLimits:AuthPerMinute"] = "10000",
        });
        _ = Factory.Server; // start now: runs the migrations
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is null)
        {
            return;
        }

        await Factory.DisposeAsync();
        await TestPostgres.DropDatabaseAsync(_databaseName);
    }

    /// <summary>Registers and signs in a new user; returns a client that sends their bearer token.</summary>
    public async Task<UserClient> NewUserAsync(string? name = null)
    {
        name ??= "user" + Guid.NewGuid().ToString("N")[..8];
        var email = $"{name}@example.test";
        const string password = "Passw0rd-123";

        var anonymous = Factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/auth/register", new { email, password })).EnsureSuccessStatusCode();
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        (await client.PutAsJsonAsync("/api/me", new UpdateMeRequest(name))).EnsureSuccessStatusCode();
        return new UserClient(client, email, token);
    }

    /// <summary>Creates a company owned by a new user.</summary>
    public async Task<(UserClient Owner, Guid CompanyId)> NewCompanyAsync()
    {
        var owner = await NewUserAsync("owner" + Guid.NewGuid().ToString("N")[..6]);
        var created = await owner.Http.PostAsJsonAsync("/api/companies", new CreateCompanyRequest("Test Kirana"));
        created.EnsureSuccessStatusCode();
        var summary = await created.Content.ReadFromJsonAsync<CompanySummary>();
        return (owner, summary!.CompanyId);
    }

    /// <summary>Owner invites <paramref name="member"/>, who accepts.</summary>
    public static async Task<MemberDto> AddMemberAsync(
        UserClient owner, Guid companyId, UserClient member, string role, IReadOnlyList<string>? custom = null)
    {
        var invite = await owner.Http.PostAsJsonAsync(
            $"/api/companies/{companyId}/invitations", new InviteRequest(member.Email, "Staff " + role, role, custom));
        invite.EnsureSuccessStatusCode();
        var token = (await invite.Content.ReadFromJsonAsync<InviteResponse>())!.Token;
        (await member.Http.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token))).EnsureSuccessStatusCode();

        var members = await owner.Http.GetFromJsonAsync<List<MemberDto>>($"/api/companies/{companyId}/members");
        return members!.Single(m => m.DisplayName == "Staff " + role);
    }

    public sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

    public sealed record UserClient(HttpClient Http, string Email, TokenResponse Token);
}

public static class FactoryExtensions
{
    /// <summary>
    /// Adds settings as the last configuration source, so they win over appsettings and any
    /// developer user secrets.
    /// </summary>
    public static WebApplicationFactory<Program> WithTestSettings(
        this WebApplicationFactory<Program> factory, Dictionary<string, string?> settings) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));
}
