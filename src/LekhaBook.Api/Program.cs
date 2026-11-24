using System.Threading.RateLimiting;
using LekhaBook.Api.Endpoints;
using LekhaBook.Api.Security;
using LekhaBook.Data.Postgres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

// PostgreSQL (books + Identity). In development the connection string comes from user secrets (README).
// Read when first needed, so later configuration sources (tests, hosting) win.
builder.Services.AddLekhaBookPostgres(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("LekhaBook")!);

// Sign-in: ASP.NET Core Identity API endpoints. Email + password, bearer access token + refresh token
// (decision D5). Company roles are NOT Identity roles; they live in CompanyMembers.
builder.Services
    .AddIdentityApiEndpoints<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<AuthDbContext>();

builder.Services.AddAuthorization();
builder.Services.AddScoped<CompanyAccess>();
builder.Services.AddScoped<IAuthorizationHandler, CompanyAccessHandler>();

// Slow down sign-up spam and password guessing per IP. Token refresh and account-info calls are not
// limited: many phones on one mobile carrier share a public IP (CGNAT) and refresh every hour.
// Per-account guessing is stopped by the Identity lockout (5 attempts).
// Behind a reverse proxy, configure UseForwardedHeaders so this sees client IPs (D4).
string[] limitedAuthPaths = ["/api/auth/login", "/api/auth/register", "/api/auth/forgotPassword", "/api/auth/resendConfirmationEmail"];
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => limitedAuthPaths.Any(p => http.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase))
        ? RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimits:AuthPerMinute", 60),
                Window = TimeSpan.FromMinutes(1),
            })
        : RateLimitPartition.GetNoLimiter("unlimited"));
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("LekhaBook")))
{
    throw new InvalidOperationException(
        "ConnectionStrings:LekhaBook is not set. See README, \"Local database\" (scripts/set-dev-database.ps1).");
}

if (app.Environment.IsDevelopment())
{
    // OpenAPI document at /openapi/v1.json
    app.MapOpenApi();

    // Development convenience: bring the local database up to date on start.
    // Production applies migrations as a deployment step instead.
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PostgresDbContext>().Database.Migrate();
        scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.Migrate();
    }
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");

// /api/auth/register, /login, /refresh, /manage/info, …
app.MapGroup("/api/auth").MapIdentityApi<AppUser>().RequireRateLimiting("auth");

app.MapCompanyEndpoints();
app.MapMasterEndpoints();

app.Run();

// Lets the integration tests start the API with WebApplicationFactory<Program>.
public partial class Program;
