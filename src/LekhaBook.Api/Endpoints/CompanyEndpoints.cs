using System.Security.Cryptography;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>Current user, companies, staff and invitations (AGENT_PLAN § 4, § 9).</summary>
public static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", GetMe);
        api.MapPut("/me", UpdateMe);
        api.MapPost("/companies", CreateCompany);
        api.MapPost("/invitations/accept", AcceptInvitation);

        // Everything under a company: members only, checked before the request body is read.
        var company = api.MapGroup("/companies/{companyId:guid}").RequireCompanyMember();
        company.MapGet("", GetCompany);
        company.MapGet("/members", ListMembers).RequirePermission(Permission.StaffView);
        company.MapPut("/members/{memberId:guid}", UpdateMember).RequirePermission(Permission.StaffEdit);
        company.MapDelete("/members/{memberId:guid}", RevokeMember).RequirePermission(Permission.StaffDelete);
        company.MapGet("/invitations", ListInvitations).RequirePermission(Permission.StaffView);
        company.MapPost("/invitations", Invite).RequirePermission(Permission.StaffCreate);
        company.MapDelete("/invitations/{invitationId:guid}", CancelInvitation).RequirePermission(Permission.StaffCreate);
    }

    // ---------------- Current user ----------------

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMe(
        HttpContext http, AuthDbContext auth, PostgresDbContext db)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var companies = await (
            from m in db.CompanyMembers
            join c in db.Companies on m.CompanyId equals c.Id
            where m.UserId == userId && m.Status == MemberStatus.Active && !c.IsDeleted
            orderby c.Name
            select new { m, c.Name }).ToListAsync();

        return TypedResults.Ok(new MeResponse(
            user.Id, user.Email!, user.DisplayName,
            companies.Select(x => Summary(x.m, x.Name)).ToList()));
    }

    private static async Task<Results<NoContent, ValidationProblem, UnauthorizedHttpResult>> UpdateMe(
        UpdateMeRequest request, HttpContext http, AuthDbContext auth)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (Invalid(request.DisplayName, 200, "displayName") is { } problem)
        {
            return problem;
        }

        user.DisplayName = request.DisplayName.Trim();
        await auth.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    // ---------------- Companies ----------------

    private static async Task<Results<Created<CompanySummary>, ValidationProblem, UnauthorizedHttpResult>> CreateCompany(
        CreateCompanyRequest request, HttpContext http, AuthDbContext auth, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (Invalid(request.Name, 200, "name") is { } problem)
        {
            return problem;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var company = new Company
        {
            Name = request.Name.Trim(),
            Phone = request.Phone?.Trim(),
            Address = request.Address?.Trim(),
            Pan = request.Pan?.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var owner = new CompanyMember
        {
            CompanyId = company.Id,
            UserId = userId,
            DisplayName = user.DisplayName ?? user.Email!,
            Role = CompanyRole.Owner,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Companies.Add(company);
        db.Accounts.AddRange(CompanySeeder.DefaultAccounts(company.Id, now, userId));
        db.CompanySyncStates.Add(new CompanySyncState { CompanyId = company.Id });
        db.CompanyMembers.Add(owner);
        await db.SaveChangesAsync();

        return TypedResults.Created($"/api/companies/{company.Id}", Summary(owner, company.Name));
    }

    private static async Task<Results<Ok<CompanyDetails>, NotFound>> GetCompany(Guid companyId, PostgresDbContext db)
    {
        var c = await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId && !x.IsDeleted);
        return c is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CompanyDetails(
                c.Id, c.Name, c.Phone, c.Email, c.Address, c.Pan,
                c.Calendar.ToString(), c.Currency, c.TimeZoneId, c.FiscalYearStartMonth));
    }

    // ---------------- Members ----------------

    private static async Task<Ok<List<MemberDto>>> ListMembers(Guid companyId, PostgresDbContext db)
    {
        var members = await db.CompanyMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .OrderBy(m => m.Role).ThenBy(m => m.DisplayName)
            .ToListAsync();
        return TypedResults.Ok(members.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<MemberDto>, NotFound, ProblemHttpResult, ValidationProblem>> UpdateMember(
        Guid companyId, Guid memberId, UpdateMemberRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();
        var target = await db.CompanyMembers.SingleOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        if (ParseRole(request.Role) is not { } role)
        {
            return RoleProblem();
        }

        if ((StaffRules.CheckManage(actor, target, revoke: false)
             ?? StaffRules.CheckGrant(actor, role, request.CustomPermissions, isNewMember: false)) is { } denied)
        {
            return Forbidden(denied);
        }

        target.Role = role;
        target.CustomPermissions = request.CustomPermissions?.Distinct().ToList();
        target.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.Ok(ToDto(target));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RevokeMember(
        Guid companyId, Guid memberId, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();
        var target = await db.CompanyMembers.SingleOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        if (StaffRules.CheckManage(actor, target, revoke: true) is { } denied)
        {
            return Forbidden(denied);
        }

        // The revoked user's sync requests now get 403, and their device wipes this company (phase 8).
        target.Status = MemberStatus.Revoked;
        target.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    // ---------------- Invitations ----------------

    private static async Task<Ok<List<InvitationDto>>> ListInvitations(Guid companyId, PostgresDbContext db, TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.Invitations.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.AcceptedAtUtc == null && i.CancelledAtUtc == null && i.ExpiresAtUtc > now)
            .OrderBy(i => i.CreatedAtUtc)
            .ToListAsync();
        return TypedResults.Ok(open.Select(i =>
            new InvitationDto(i.Id, i.NormalizedEmail.ToLowerInvariant(), i.DisplayName, i.Role.ToString(), i.ExpiresAtUtc)).ToList());
    }

    private static async Task<Results<Ok<InviteResponse>, ProblemHttpResult, ValidationProblem>> Invite(
        Guid companyId, InviteRequest request, CompanyAccess access, PostgresDbContext db, AuthDbContext auth, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();

        if (Invalid(request.DisplayName, 200, "displayName") is { } nameProblem)
        {
            return nameProblem;
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@') || request.Email.Length > 256)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."] });
        }

        if (ParseRole(request.Role) is not { } role)
        {
            return RoleProblem();
        }

        if (StaffRules.CheckGrant(actor, role, request.CustomPermissions, isNewMember: true) is { } denied)
        {
            return Forbidden(denied);
        }

        var email = Invitation.NormalizeEmail(request.Email);
        var existingUserId = await auth.Users.Where(u => u.NormalizedEmail == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync();
        if (existingUserId is { } uid
            && await db.CompanyMembers.AnyAsync(m => m.CompanyId == companyId && m.UserId == uid && m.Status == MemberStatus.Active))
        {
            return TypedResults.Problem("That person is already a member of this company.", statusCode: StatusCodes.Status409Conflict);
        }

        var token = Base64UrlToken();
        var now = clock.GetUtcNow().UtcDateTime;
        var invitation = new Invitation
        {
            CompanyId = companyId,
            NormalizedEmail = email,
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            CustomPermissions = request.CustomPermissions?.Distinct().ToList(),
            TokenHash = Hash(token),
            InvitedBy = actor.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + Invitation.Lifetime,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();

        return TypedResults.Ok(new InviteResponse(invitation.Id, token, invitation.ExpiresAtUtc));
    }

    private static async Task<Results<NoContent, NotFound>> CancelInvitation(
        Guid companyId, Guid invitationId, PostgresDbContext db, TimeProvider clock)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.CompanyId == companyId);
        if (invitation is null || invitation.AcceptedAtUtc is not null)
        {
            return TypedResults.NotFound();
        }

        invitation.CancelledAtUtc ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CompanySummary>, ProblemHttpResult, UnauthorizedHttpResult>> AcceptInvitation(
        AcceptInvitationRequest request, HttpContext http, AuthDbContext auth, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(request.Token ?? string.Empty);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash);
        if (invitation is null || !invitation.IsOpen(now))
        {
            return TypedResults.Problem("This invitation is invalid, expired or already used.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(user.NormalizedEmail, invitation.NormalizedEmail, StringComparison.Ordinal))
        {
            return Forbidden("This invitation was sent to a different email address.");
        }

        var member = await db.CompanyMembers.SingleOrDefaultAsync(m => m.CompanyId == invitation.CompanyId && m.UserId == userId);
        if (member is { IsActive: true })
        {
            return TypedResults.Problem("You are already a member of this company.", statusCode: StatusCodes.Status409Conflict);
        }

        if (member is null)
        {
            member = new CompanyMember { CompanyId = invitation.CompanyId, UserId = userId, DisplayName = invitation.DisplayName, JoinedAtUtc = now };
            db.CompanyMembers.Add(member);
        }

        // A previously revoked member comes back with the access in the new invitation.
        member.Role = invitation.Role;
        member.CustomPermissions = invitation.CustomPermissions;
        member.Status = MemberStatus.Active;
        member.DisplayName = invitation.DisplayName;
        member.UpdatedAtUtc = now;
        invitation.AcceptedAtUtc = now;
        invitation.AcceptedBy = userId;
        await db.SaveChangesAsync();

        var companyName = await db.Companies.Where(c => c.Id == invitation.CompanyId).Select(c => c.Name).SingleAsync();
        return TypedResults.Ok(Summary(member, companyName));
    }

    // ---------------- Helpers ----------------

    private static CompanySummary Summary(CompanyMember m, string companyName) =>
        new(m.CompanyId, companyName, m.Role.ToString(), m.EffectivePermissions.Order(StringComparer.Ordinal).ToList());

    private static MemberDto ToDto(CompanyMember m) =>
        new(m.Id, m.UserId, m.DisplayName, m.Role.ToString(), m.Status.ToString(), m.CustomPermissions,
            m.EffectivePermissions.Order(StringComparer.Ordinal).ToList(), m.JoinedAtUtc);

    private static CompanyRole? ParseRole(string? role) =>
        Enum.TryParse<CompanyRole>(role, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r : null;

    private static ValidationProblem RoleProblem() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["role"] = [$"Role must be one of: {string.Join(", ", Enum.GetNames<CompanyRole>())}."],
        });

    private static ValidationProblem? Invalid(string? value, int maxLength, string field) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [$"Required, at most {maxLength} characters."] })
            : null;

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static string Base64UrlToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
