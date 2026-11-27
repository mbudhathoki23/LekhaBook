using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;

namespace LekhaBook.Application.Tests;

public sealed class TwoFactorSignInTests
{
    [Fact]
    public async Task A_two_factor_challenge_does_not_issue_or_save_a_session()
    {
        var store = new Store();
        var api = new LekhaBookApi(new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { detail = "RequiresTwoFactor" }),
        })) { BaseAddress = new Uri("https://example.test") }, store, TimeProvider.System);
        var session = new SessionService(api, store, new Settings(), []);

        var result = await session.SignInAsync("user@example.test", "not-a-real-password", null);

        Assert.True(result.RequiresTwoFactor);
        Assert.False(result.Ok);
        Assert.False(session.IsSignedIn);
        Assert.False(api.HasTokens);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codes_are_sent_to_the_correct_identity_login_field(bool recovery)
    {
        JsonElement body = default;
        var store = new Store();
        var api = new LekhaBookApi(new HttpClient(new Handler(request =>
        {
            body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { accessToken = "access", refreshToken = "refresh", expiresIn = 3600 }),
            };
        })) { BaseAddress = new Uri("https://example.test") }, store, TimeProvider.System);

        var result = await api.LoginAsync("user@example.test", "not-a-real-password", recovery ? null : "123456", recovery ? "abcde-fghij" : null);

        Assert.True(result.Ok);
        Assert.Equal(recovery ? "abcde-fghij" : "123456", body.GetProperty(recovery ? "twoFactorRecoveryCode" : "twoFactorCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty(recovery ? "twoFactorCode" : "twoFactorRecoveryCode").ValueKind);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Store : ITokenStore
    {
        public int Saves { get; private set; }
        public Task<ApiTokens?> LoadAsync() => Task.FromResult<ApiTokens?>(null);
        public Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl) { Saves++; return Task.FromResult(false); }
        public Task UpdateAsync(ApiTokens tokens) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
    private sealed class Settings : ILocalSettings
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string? value) => Task.CompletedTask;
    }
}
