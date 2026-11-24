using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

/// <summary>
/// HTTP calls to LekhaBook.Api. Sets the bearer token on each request itself (not via a DI
/// DelegatingHandler, whose scope would differ on the web host), refreshes once on 401 and retries.
/// Register per user session: Scoped on the web host, Singleton in the MAUI app.
/// </summary>
public sealed class LekhaBookApi(HttpClient http, ITokenStore tokenStore, TimeProvider clock)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ApiTokens? _tokens;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Raised when the server rejects the refresh token: the user must sign in again.</summary>
    public event EventHandler? SessionExpired;

    public bool HasTokens => _tokens is not null;

    public void SetTokens(ApiTokens? tokens) => _tokens = tokens;

    // ---------------- Auth ----------------

    public async Task<ApiResult<ApiTokens>> LoginAsync(string email, string password)
    {
        var result = await SendAsync<TokenResponse>(HttpMethod.Post, "/api/auth/login", new { email, password }, authenticated: false);
        if (!result.Ok)
        {
            return new(null, result.StatusCode == 401 ? "Email or password is incorrect." : result.Error, result.StatusCode);
        }

        _tokens = ToTokens(result.Value!);
        return new(_tokens, null, 200);
    }

    public async Task<ApiResult<bool>> RegisterAsync(string email, string password)
    {
        var result = await SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/register", new { email, password }, authenticated: false);
        return new(result.Ok, result.Error, result.StatusCode);
    }

    /// <summary>
    /// Gets a new access token. Returns true on success; false if the server rejected the refresh token
    /// (raises <see cref="SessionExpired"/>); throws <see cref="HttpRequestException"/> when offline.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (_tokens is not { } current)
        {
            return false;
        }

        await _refreshLock.WaitAsync();
        try
        {
            // Another call may have refreshed while we waited.
            if (!ReferenceEquals(current, _tokens) && _tokens?.AccessToken is not null)
            {
                return true;
            }

            using var response = await http.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = _tokens!.RefreshToken }, Json);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                _tokens = null;
                SessionExpired?.Invoke(this, EventArgs.Empty);
                return false;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(Json);
            _tokens = ToTokens(body!);
            await tokenStore.UpdateAsync(_tokens);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ---------------- Generic send ----------------

    public Task<ApiResult<T>> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated = true)
    {
        try
        {
            if (authenticated && _tokens is { } t && (t.AccessToken is null || t.AccessTokenExpiresAtUtc <= clock.GetUtcNow().UtcDateTime.AddSeconds(30)))
            {
                if (!await RefreshAsync())
                {
                    return new(default, "Please sign in again.", 401);
                }
            }

            using var response = await SendOnceAsync(method, path, body, authenticated);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && _tokens is not null)
            {
                if (!await RefreshAsync())
                {
                    return new(default, "Please sign in again.", 401);
                }

                using var retry = await SendOnceAsync(method, path, body, authenticated);
                return await ReadAsync<T>(retry);
            }

            return await ReadAsync<T>(response);
        }
        catch (HttpRequestException)
        {
            return new(default, "Can't reach the server. Check your internet connection.", 0);
        }
        catch (TaskCanceledException)
        {
            return new(default, "The server took too long to answer.", 0);
        }
    }

    private Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool authenticated)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (authenticated && _tokens?.AccessToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http.SendAsync(request);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return new(typeof(T) == typeof(bool) ? (T)(object)true : default, null, status);
            }

            return new(await response.Content.ReadFromJsonAsync<T>(Json), null, status);
        }

        return new(default, await ErrorMessageAsync(response), status);
    }

    /// <summary>Turns ProblemDetails / validation errors / Identity errors into one readable message.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var fallback = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You don't have permission to do that.",
            HttpStatusCode.NotFound => "Not found.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a minute and try again.",
            _ => $"Something went wrong ({(int)response.StatusCode}).",
        };

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(v => v.GetString()) : [])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
                if (messages.Count > 0)
                {
                    return string.Join(" ", messages);
                }
            }

            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d)
            {
                return d;
            }
        }
        catch (JsonException)
        {
            // Not JSON: use the fallback.
        }

        return fallback;
    }

    private ApiTokens ToTokens(TokenResponse r) =>
        new(r.AccessToken, r.RefreshToken, clock.GetUtcNow().UtcDateTime.AddSeconds(r.ExpiresIn));

    private sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);
}
