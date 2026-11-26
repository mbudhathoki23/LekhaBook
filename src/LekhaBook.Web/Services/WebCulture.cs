using System.Globalization;
using LekhaBook.UI.Localization;
using LekhaBook.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;

namespace LekhaBook.Web.Services;

/// <summary>
/// Language on the web (decision D13 applies to sign-in only). The choice is a culture cookie that
/// RequestLocalization reads on every request. Interactive components can't set cookies, so the picker
/// navigates to /culture/set, which sets the cookie and returns to the same page.
/// </summary>
public static class WebCulture
{
    public static void MapWebCultureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/culture/set", (string? culture, string? redirectUri, HttpContext http) =>
        {
            var code = Languages.Normalize(culture);
            http.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(code, code)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                });

            return Results.LocalRedirect(IsLocalPath(redirectUri) ? redirectUri! : "/");
        });
    }

    /// <summary>A path inside this app, such as "/staff?x=1". Rejects "//host" and anything with a scheme.</summary>
    private static bool IsLocalPath(string? path) =>
        path is { Length: > 1 } p && p[0] == '/' && p[1] != '/' && p[1] != '\\';
}

/// <summary>
/// The web host's <see cref="ILanguageService"/>. The culture comes from the request, so a change navigates
/// to /culture/set and reloads the page; nothing is raised on the current screen.
/// </summary>
public sealed class WebLanguageService(NavigationManager navigation) : ILanguageService
{
    public string CurrentCode => Languages.Normalize(CultureInfo.CurrentUICulture.Name);

    // The page reloads on a change, so there is never an in-place update to announce.
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task<bool> SetAsync(string code)
    {
        var here = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        var url = $"/culture/set?culture={Uri.EscapeDataString(Languages.Normalize(code))}&redirectUri={Uri.EscapeDataString(here)}";
        navigation.NavigateTo(url, forceLoad: true);
        return Task.FromResult(true);
    }
}
