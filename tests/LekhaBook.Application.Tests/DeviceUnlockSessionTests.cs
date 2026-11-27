using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Application.Tests;

public sealed class DeviceUnlockSessionTests
{
    private static readonly MeResponse Profile = new(Guid.CreateVersion7(), "unlock@example.test", "Unlock test", []);

    [Fact]
    public async Task Cancelled_device_unlock_does_not_read_tokens_cached_profile_or_api()
    {
        var guard = new Guard { Enabled = true, Verified = false };
        var (session, store, calls, settings) = Create(guard);
        settings.Values["session.me"] = JsonSerializer.Serialize(Profile);

        await Task.WhenAll(session.InitializeAsync(), session.InitializeAsync());

        Assert.True(session.IsInitialized);
        Assert.True(session.IsLocked);
        Assert.False(session.IsSignedIn);
        Assert.Null(session.Me);
        Assert.Equal(0, store.Loads);
        Assert.Empty(calls);
        Assert.Equal(1, guard.Prompts);
    }

    [Fact]
    public async Task Successful_device_unlock_restores_the_saved_session_without_password_login()
    {
        var guard = new Guard { Enabled = true, Verified = true };
        var (session, store, calls, _) = Create(guard);

        await session.InitializeAsync();

        Assert.True(session.IsSignedIn);
        Assert.False(session.IsLocked);
        Assert.Equal(Profile.UserId, session.Me!.UserId);
        Assert.Equal(1, store.Loads);
        Assert.Equal(["/api/me"], calls);
    }

    [Fact]
    public async Task Device_unlock_can_be_retried_after_cancel_without_exposing_the_saved_session()
    {
        var guard = new Guard { Enabled = true };
        var (session, store, _, _) = Create(guard);
        await session.InitializeAsync();
        Assert.Equal(0, store.Loads);

        guard.Verified = true;
        var result = await session.UnlockAsync();

        Assert.True(result.Ok);
        Assert.True(session.IsSignedIn);
        Assert.False(session.IsLocked);
        Assert.Equal(2, guard.Prompts);
    }

    [Fact]
    public async Task Offline_cached_sign_in_is_available_only_after_successful_device_verification()
    {
        var guard = new Guard { Enabled = true, Verified = true };
        var (session, _, _, settings) = Create(guard, offline: true);
        settings.Values["session.me"] = JsonSerializer.Serialize(Profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await session.InitializeAsync();
        Assert.True(session.IsSignedIn);
        Assert.True(session.IsOffline);
        Assert.False(session.IsLocked);
        Assert.Equal(Profile.UserId, session.Me!.UserId);
    }

    [Fact]
    public async Task A_different_account_must_opt_in_to_device_unlock_again()
    {
        var guard = new Guard { Enabled = true, EnabledUser = Guid.CreateVersion7() };
        var (session, _, _, _) = Create(guard);

        var result = await session.SignInAsync(Profile.Email, "not-a-real-password", null);

        Assert.True(result.Ok);
        Assert.False(guard.Enabled);
        Assert.Equal(1, guard.Forgotten);
    }

    [Fact]
    public async Task Sign_out_forgets_the_device_unlock_opt_in_and_saved_tokens()
    {
        var guard = new Guard { Enabled = true, Verified = true, EnabledUser = Profile.UserId };
        var (session, store, _, _) = Create(guard);
        await session.InitializeAsync();
        await session.SignOutAsync();
        Assert.False(session.IsSignedIn);
        Assert.False(session.IsLocked);
        Assert.False(guard.Enabled);
        Assert.Null(store.Tokens);
    }

    private static (SessionService Session, Store Store, List<string> Calls, Settings Settings) Create(Guard guard, bool offline = false)
    {
        var calls = new List<string>();
        var store = new Store();
        var settings = new Settings();
        var api = new LekhaBookApi(new HttpClient(new Handler(request =>
        {
            if (offline) throw new HttpRequestException("Offline test");
            calls.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/api/auth/login"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = "access", refreshToken = "refresh", expiresIn = 3600, tokenType = "Bearer" }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Profile) };
        })) { BaseAddress = new Uri("https://example.test") }, store, TimeProvider.System);
        return (new SessionService(api, store, settings, [], guard), store, calls, settings);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class Store : ITokenStore
    {
        public ApiTokens? Tokens { get; set; } = new("access", "refresh", DateTime.UtcNow.AddHours(1));
        public int Loads { get; private set; }
        public Task<ApiTokens?> LoadAsync() { Loads++; return Task.FromResult(Tokens); }
        public Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl) { Tokens = tokens; return Task.FromResult(false); }
        public Task UpdateAsync(ApiTokens tokens) { Tokens = tokens; return Task.CompletedTask; }
        public Task ClearAsync() { Tokens = null; return Task.CompletedTask; }
    }

    private sealed class Settings : ILocalSettings
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string? value) { if (value is null) Values.Remove(key); else Values[key] = value; return Task.CompletedTask; }
    }

    private sealed class Guard : IDeviceUnlock
    {
        public bool Enabled { get; set; }
        public bool Verified { get; set; }
        public Guid EnabledUser { get; set; }
        public int Prompts { get; private set; }
        public int Forgotten { get; private set; }
        public Task<DeviceUnlockState> GetStateAsync() => Task.FromResult(new DeviceUnlockState(true, true, Enabled));
        public Task<DeviceUnlockResult> UnlockAsync() { Prompts++; return Task.FromResult(new DeviceUnlockResult(Verified, Verified ? null : "Cancelled")); }
        public Task<DeviceUnlockResult> EnableAsync(Guid userId) => Task.FromResult(new DeviceUnlockResult(false));
        public Task<DeviceUnlockResult> DisableAsync() => Task.FromResult(new DeviceUnlockResult(false));
        public Task<bool> IsEnabledForAsync(Guid userId) => Task.FromResult(Enabled && EnabledUser == userId);
        public Task ForgetAsync() { Enabled = false; Forgotten++; return Task.CompletedTask; }
    }
}
