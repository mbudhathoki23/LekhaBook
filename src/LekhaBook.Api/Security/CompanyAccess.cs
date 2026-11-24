using System.Security.Claims;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Security;

/// <summary>
/// Requires the signed-in user to be an active member of the company in the route's {companyId},
/// and optionally to hold <see cref="Permission"/>. Runs as authorization, so a non-member gets 403
/// before any request body is read.
/// </summary>
public sealed record CompanyAccessRequirement(string? Permission) : IAuthorizationRequirement;

public sealed class CompanyAccessHandler(CompanyAccess access) : AuthorizationHandler<CompanyAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CompanyAccessRequirement requirement)
    {
        var member = await access.GetMemberAsync();
        if (member is { IsActive: true } && (requirement.Permission is null || member.Has(requirement.Permission)))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Loads (once per request) the current user's membership in the route's company.</summary>
public sealed class CompanyAccess(IHttpContextAccessor http, PostgresDbContext db)
{
    private bool _loaded;
    private CompanyMember? _member;

    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public Guid? CompanyId =>
        http.HttpContext?.Request.RouteValues.TryGetValue("companyId", out var raw) == true
        && Guid.TryParse(raw?.ToString(), out var id) ? id : null;

    /// <summary>The membership, or null if the user isn't in this company (or there is no company in the route).</summary>
    public async Task<CompanyMember?> GetMemberAsync()
    {
        if (_loaded)
        {
            return _member;
        }

        _loaded = true;
        if (http.HttpContext?.User is { } user && UserId(user) is { } userId && CompanyId is { } companyId)
        {
            _member = await db.CompanyMembers.SingleOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId);
        }

        return _member;
    }

    /// <summary>For endpoints already guarded by <see cref="CompanyAccessRequirement"/>.</summary>
    public async Task<CompanyMember> RequireMemberAsync() =>
        await GetMemberAsync() ?? throw new InvalidOperationException("Endpoint is missing the company access policy.");
}

public static class CompanyAccessExtensions
{
    /// <summary>Only active members of the route's company.</summary>
    public static TBuilder RequireCompanyMember<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new CompanyAccessRequirement(null))
            .Build());

    /// <summary>Only active members of the route's company who hold <paramref name="permission"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new CompanyAccessRequirement(permission))
            .Build());
}
