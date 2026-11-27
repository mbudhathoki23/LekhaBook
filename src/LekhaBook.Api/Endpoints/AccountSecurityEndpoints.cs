using System.Security.Claims;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using Microsoft.AspNetCore.Identity;

namespace LekhaBook.Api.Endpoints;

public static class AccountSecurityEndpoints
{
    public static void MapAccountSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/account/security/two-factor", async (HttpContext http, UserManager<AppUser> users) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.Unauthorized();
            // Reading account settings must not generate an authenticator key or rotate the security stamp.
            return Results.Ok(new TwoFactorState(string.Empty, await users.CountRecoveryCodesAsync(user), null,
                await users.GetTwoFactorEnabledAsync(user), false));
        }).RequireAuthorization();
    }
}
