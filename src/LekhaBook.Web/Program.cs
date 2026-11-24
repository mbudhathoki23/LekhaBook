using LekhaBook.Client;
using LekhaBook.UI;
using LekhaBook.UI.Services;
using LekhaBook.Web.Components;
using LekhaBook.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Shared UI + browser-host implementations (the web app is online-only in v1, decision D1).
builder.Services.AddLekhaBookUI();
builder.Services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
builder.Services.AddSingleton<IConnectivityService, WebConnectivityService>();
builder.Services.AddSingleton<ISyncService, OnlineOnlySyncService>();

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseBrowserBinding();
app.UseAuthentication();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapWebSignInEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(LekhaBook.UI.Routes).Assembly);

app.Run();
