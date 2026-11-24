using System.Security.Claims;
using System.Security.Cryptography;
using LekhaBook.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.JSInterop;

namespace LekhaBook.Web.Services;

/// <summary>
/// Web sign-in (decision D13). The refresh token lives in an HttpOnly auth cookie that only this server
/// can read. Each Blazor circuit gets it once at start (passed to Routes), exchanges it for an access
/// token, and keeps tokens in memory. Interactive components can't set cookies, so sign-in and
/// sign-out go through a one-time, browser-bound ticket and a full-page redirect.
/// </summary>
public static class WebSignIn
{
    public const string BindingCookie = "lb.bind";
    private const string TicketPrefix = "signin-ticket:";
    private const string LogoutPrefix = "signout-nonce:";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    private sealed record Ticket(string RefreshToken, string Binding);

    /// <summary>A path inside this app (no scheme, host or leading slash), e.g. "staff" or "invite?token=x".</summary>
    private static bool IsSafeLocal(string? path) =>
        path is { Length: > 0 } p && !p.Contains("//", StringComparison.Ordinal) && !p.Contains(':')
        && !p.StartsWith('/') && !p.StartsWith('\\');

    public static string NewSecret() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Gives every browser a random HttpOnly "binding" cookie before the app renders. Sign-in tickets are
    /// tied to it, so a ticket link sent to someone else's browser can't sign them in (login CSRF).
    /// </summary>
    public static IApplicationBuilder UseBrowserBinding(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!http.Request.Cookies.TryGetValue(BindingCookie, out var binding) || string.IsNullOrEmpty(binding))
        {
            binding = NewSecret();
            http.Response.Cookies.Append(BindingCookie, binding, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365),
            });
        }

        http.Items[BindingCookie] = binding;
        await next();
    });

    public static string? CurrentBinding(HttpContext http) =>
        http.Items[BindingCookie] as string ?? http.Request.Cookies[BindingCookie];

    internal static string IssueTicket(IMemoryCache cache, string refreshToken, string binding)
    {
        var ticket = NewSecret();
        cache.Set(TicketPrefix + ticket, new Ticket(refreshToken, binding), TicketLifetime);
        return ticket;
    }

    internal static string IssueLogoutNonce(IMemoryCache cache, string binding)
    {
        var nonce = NewSecret();
        cache.Set(LogoutPrefix + nonce, binding, TicketLifetime);
        return nonce;
    }

    public static void MapWebSignInEndpoints(this IEndpointRouteBuilder app)
    {
        // Exchanges a one-time ticket (from the circuit) for the auth cookie, then leaves the URL at once.
        app.MapGet("/account/complete", async (string ticket, string? returnUrl, HttpContext http, IMemoryCache cache) =>
        {
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var key = TicketPrefix + ticket;
            if (!cache.TryGetValue(key, out Ticket? data) || data is null)
            {
                return Results.Redirect("/signin?error=expired");
            }

            cache.Remove(key); // single use
            if (!string.Equals(CurrentBinding(http), data.Binding, StringComparison.Ordinal))
            {
                return Results.Redirect("/signin?error=expired");
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "lekhabook-user")], CookieAuthenticationDefaults.AuthenticationScheme));
            var properties = new AuthenticationProperties { IsPersistent = true };
            properties.StoreTokens([new AuthenticationToken { Name = "refresh_token", Value = data.RefreshToken }]);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
            return Results.LocalRedirect(IsSafeLocal(returnUrl) ? "/" + returnUrl : "/");
        });

        app.MapGet("/account/logout", async (string nonce, HttpContext http, IMemoryCache cache) =>
        {
            var key = LogoutPrefix + nonce;
            if (cache.TryGetValue(key, out string? binding) && string.Equals(binding, CurrentBinding(http), StringComparison.Ordinal))
            {
                cache.Remove(key);
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return Results.Redirect("/signin");
        });
    }
}

/// <summary>Per-circuit token holder for the web host. Registered Scoped: one per browser circuit.</summary>
public sealed class WebTokenStore(NavigationManager navigation, IMemoryCache cache) : ITokenStore
{
    private ApiTokens? _current;

    /// <summary>Set by Routes when the circuit starts, from the auth cookie (encrypted in the page).</summary>
    public string? InitialRefreshToken { get; set; }

    public string? BrowserBinding { get; set; }

    public Task<ApiTokens?> LoadAsync() => Task.FromResult(
        _current ?? (InitialRefreshToken is { Length: > 0 } refresh ? new ApiTokens(null, refresh, DateTime.MinValue) : null));

    public Task<bool> SaveAsync(ApiTokens tokens, string? returnUrl)
    {
        _current = tokens;
        if (BrowserBinding is not { } binding)
        {
            return Task.FromResult(false);
        }

        var ticket = WebSignIn.IssueTicket(cache, tokens.RefreshToken, binding);
        var url = $"/account/complete?ticket={Uri.EscapeDataString(ticket)}";
        if (returnUrl is { Length: > 0 })
        {
            url += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        navigation.NavigateTo(url, forceLoad: true);
        return Task.FromResult(true);
    }

    public Task UpdateAsync(ApiTokens tokens)
    {
        // Refreshed tokens stay in this circuit; the cookie keeps the refresh token it was given.
        _current = tokens;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _current = null;
        InitialRefreshToken = null;
        var nonce = BrowserBinding is { } binding ? WebSignIn.IssueLogoutNonce(cache, binding) : "none";
        navigation.NavigateTo($"/account/logout?nonce={Uri.EscapeDataString(nonce)}", forceLoad: true);
        return Task.CompletedTask;
    }
}

/// <summary>Small settings in the browser's localStorage, encrypted by the server (per circuit).</summary>
public sealed class WebLocalSettings(ProtectedLocalStorage storage) : ILocalSettings
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var result = await storage.GetAsync<string>(key);
            return result.Success ? result.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or JSException)
        {
            return null; // not available yet, or written by an older key
        }
    }

    public async Task SetAsync(string key, string? value)
    {
        try
        {
            if (value is null)
            {
                await storage.DeleteAsync(key);
            }
            else
            {
                await storage.SetAsync(key, value);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
            // Browser gone or not ready: the setting is a convenience only.
        }
    }
}
