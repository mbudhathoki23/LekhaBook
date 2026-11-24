using System.Net;
using System.Text;
using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Application.Tests;

public sealed class SessionServiceTests
{
    private static readonly MeResponse Me = new(
        Guid.CreateVersion7(), "ram@example.test", "Ram",
        [new CompanySummary(Guid.CreateVersion7(), "Ram Kirana", "Owner", ["staff.view"])]);

    // ---------------- DI lifetimes ----------------

    [Fact]
    public void Web_host_gives_each_circuit_its_own_session_and_tokens()
    {
        using var provider = Services(perUserScope: true);
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        Assert.NotSame(circuitA.ServiceProvider.GetRequiredService<ISessionService>(), circuitB.ServiceProvider.GetRequiredService<ISessionService>());
        Assert.NotSame(circuitA.ServiceProvider.GetRequiredService<LekhaBookApi>(), circuitB.ServiceProvider.GetRequiredService<LekhaBookApi>());
        Assert.Same(circuitA.ServiceProvider.GetRequiredService<ISessionService>(), circuitA.ServiceProvider.GetRequiredService<ISessionService>());
    }

    [Fact]
    public void App_host_has_one_session()
    {
        using var provider = Services(perUserScope: false);
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();

        Assert.Same(a.ServiceProvider.GetRequiredService<ISessionService>(), b.ServiceProvider.GetRequiredService<ISessionService>());
    }

    // ---------------- Offline-first behaviour ----------------

    [Fact]
    public async Task Saved_session_restores_from_the_server()
    {
        var (session, store, _) = Build(_ => Json(Me));
        store.Saved = Tokens();

        await session.InitializeAsync();

        Assert.True(session.IsSignedIn);
        Assert.False(session.IsOffline);
        Assert.Equal("Ram Kirana", session.CompanyName);
        Assert.True(session.Can("staff.view"));
    }

    [Fact]
    public async Task Offline_start_stays_signed_in_with_the_cached_profile()
    {
        var settings = new MemorySettings();
        settings.Values["session.me"] = JsonSerializer.Serialize(Me, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var (session, store, _) = Build(_ => throw new HttpRequestException("No network"), settings);
        store.Saved = Tokens();

        await session.InitializeAsync();

        Assert.True(session.IsSignedIn);
        Assert.True(session.IsOffline);
        Assert.Equal("Ram Kirana", session.CompanyName);
        Assert.False(store.Cleared);
    }

    /// <summary>
    /// The common real case: the phone is opened offline the next morning. The access token has expired,
    /// so the refresh is attempted first and fails on the network. The user must stay signed in.
    /// </summary>
    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Offline_start_with_an_expired_token_stays_signed_in(string failure)
    {
        var settings = new MemorySettings();
        settings.Values["session.me"] = JsonSerializer.Serialize(Me, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var (session, store, _) = Build(_ => failure == "network"
            ? throw new HttpRequestException("No network")
            : throw new TaskCanceledException("Timed out"), settings);
        store.Saved = new ApiTokens("yesterdays-access", "refresh", DateTime.UtcNow.AddHours(-10));

        await session.InitializeAsync();

        Assert.True(session.IsSignedIn);
        Assert.True(session.IsOffline);
        Assert.False(store.Cleared);
        Assert.Equal("Ram Kirana", session.CompanyName);
    }

    [Fact]
    public async Task Rejected_refresh_token_clears_stored_tokens_once()
    {
        var (session, store, _) = Build(request => request.RequestUri!.AbsolutePath == "/api/auth/refresh"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json(Me));
        store.Saved = Tokens(accessToken: null);

        await session.InitializeAsync();

        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task Rejected_refresh_token_signs_out_and_clears_tokens()
    {
        var (session, store, _) = Build(request => request.RequestUri!.AbsolutePath == "/api/auth/refresh"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json(Me));
        store.Saved = Tokens(accessToken: null); // forces a refresh first

        await session.InitializeAsync();

        Assert.False(session.IsSignedIn);
        Assert.True(store.Cleared);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once_and_the_call_retried()
    {
        var calls = new List<string>();
        var (session, store, _) = Build(request =>
        {
            calls.Add($"{request.RequestUri!.AbsolutePath} {request.Headers.Authorization?.Parameter}");
            return request.RequestUri.AbsolutePath switch
            {
                "/api/auth/refresh" => Json(new { tokenType = "Bearer", accessToken = "new-access", expiresIn = 3600, refreshToken = "new-refresh" }),
                "/api/me" when request.Headers.Authorization?.Parameter == "old-access" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => Json(Me),
            };
        });
        store.Saved = Tokens(accessToken: "old-access");

        await session.InitializeAsync();

        Assert.True(session.IsSignedIn);
        Assert.Equal(["/api/me old-access", "/api/auth/refresh ", "/api/me new-access"], calls);
        Assert.Equal("new-refresh", store.Updated?.RefreshToken);
    }

    // ---------------- Helpers ----------------

    private static ApiTokens Tokens(string? accessToken = "access") =>
        new(accessToken, "refresh", DateTime.UtcNow.AddHours(1));

    private static ServiceProvider Services(bool perUserScope)
    {
        var services = new ServiceCollection();
        services.AddLekhaBookClient(new Uri("http://localhost:5050/"), perUserScope);
        if (perUserScope)
        {
            services.AddScoped<ITokenStore, MemoryTokenStore>();
            services.AddScoped<ILocalSettings, MemorySettings>();
        }
        else
        {
            services.AddSingleton<ITokenStore, MemoryTokenStore>();
            services.AddSingleton<ILocalSettings, MemorySettings>();
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static (SessionService Session, MemoryTokenStore Store, MemorySettings Settings) Build(
        Func<HttpRequestMessage, HttpResponseMessage> handler, MemorySettings? settings = null)
    {
        var store = new MemoryTokenStore();
        settings ??= new MemorySettings();
        var http = new HttpClient(new FakeHandler(handler)) { BaseAddress = new Uri("http://localhost:5050/") };
        var api = new LekhaBookApi(http, store, TimeProvider.System);
        return (new SessionService(api, store, settings, []), store, settings);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class MemoryTokenStore : ITokenStore
    {
        public ApiTokens? Saved { get; set; }

        public ApiTokens? Updated { get; private set; }

        public bool Cleared { get; private set; }

        public int ClearCount { get; private set; }

        public Task<ApiTokens?> LoadAsync() => Task.FromResult(Saved);

        public Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl)
        {
            Saved = tokens;
            return Task.FromResult(false);
        }

        public Task UpdateAsync(ApiTokens tokens)
        {
            Updated = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            Cleared = true;
            ClearCount++;
            Saved = null;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySettings : ILocalSettings
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value)
        {
            if (value is null)
            {
                Values.Remove(key);
            }
            else
            {
                Values[key] = value;
            }

            return Task.CompletedTask;
        }
    }
}
