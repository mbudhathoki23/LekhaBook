using LekhaBook.Client;
using LekhaBook.UI;
using LekhaBook.UI.Localization;
using LekhaBook.UI.Services;
using LekhaBook.Web.Components;
using LekhaBook.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
        .SetApplicationName("LekhaBook.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

if (builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Shared UI + browser-host implementations (the web app is online-only in v1, decision D1).
builder.Services.AddLekhaBookUI();
builder.Services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
builder.Services.AddSingleton<IDocumentCapture, BrowserDocumentCapture>();
builder.Services.AddSingleton<IConnectivityService, WebConnectivityService>();
builder.Services.AddSingleton<ISyncService, OnlineOnlySyncService>();
builder.Services.AddScoped<ILanguageService, WebLanguageService>(); // culture cookie, see WebCulture

// Sign-in (D13). Everything that holds a user's tokens is Scoped = one per browser circuit.
var apiBase = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5050/");
builder.Services.AddLekhaBookClient(apiBase, perUserScope: true);
builder.Services.AddScoped<WebTokenStore>();
builder.Services.AddScoped<ITokenStore>(sp => sp.GetRequiredService<WebTokenStore>());
builder.Services.AddScoped<ILocalSettings, WebLocalSettings>();
builder.Services.AddMemoryCache(); // sign-in tickets; single server instance only (D4)

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "lb.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // Matches the refresh token inside it, which isn't renewed in the cookie: web users sign in
        // again after 14 days (D13). Sliding renewal would only outlive the token it carries.
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = false;
        o.LoginPath = "/signin";
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddPublicReports(); // public report links, see PublicReports

var app = builder.Build();

if (app.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    app.UseHttpsRedirection();
}

// English unless the culture cookie (set by /culture/set) asks for another language.
// CultureFor keeps Gregorian dates and "." decimals in every language.
var cultures = Languages.Supported.Select(l => Languages.CultureFor(l.Code)).ToList();
var localization = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(Languages.English),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
};
// Only the cookie chooses. The default providers would also follow ?culture= and the browser language.
localization.RequestCultureProviders = [new CookieRequestCultureProvider()];
app.UseRequestLocalization(localization);

app.UseBrowserBinding();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapWebSignInEndpoints();
app.MapWebCultureEndpoints();
app.MapPublicReports();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(LekhaBook.UI.Routes).Assembly);

app.Run();
