namespace LekhaBook.Contracts;

// Shapes exchanged between LekhaBook.Api and its clients (MAUI app, web app).
// Roles and permissions travel as strings so clients don't depend on the domain assembly.

public sealed record MeResponse(Guid UserId, string Email, string? DisplayName, IReadOnlyList<CompanySummary> Companies);

public sealed record UpdateMeRequest(string DisplayName);

/// <summary><paramref name="Calendar"/> is "BS" (Nepali miti, the default when null) or "AD".</summary>
public sealed record CreateCompanyRequest(string Name, string? Phone = null, string? Address = null, string? Pan = null, string? Calendar = null);

/// <summary>
/// A company the current user can open, with their role and effective permissions in it.
/// <paramref name="Calendar"/> ("BS" or "AD") is how its dates are shown and typed; they are stored and sent as AD.
/// </summary>
public sealed record CompanySummary(Guid CompanyId, string Name, string Role, IReadOnlyList<string> Permissions, string Calendar = "BS");

public sealed record UpdateCalendarRequest(string Calendar);

public sealed record CompanyDetails(
    Guid CompanyId, string Name, string? Phone, string? Email, string? Address, string? Pan,
    string Calendar, string Currency, string TimeZoneId, int FiscalYearStartMonth, bool HasLogo = false);

public sealed record MemberDto(
    Guid MemberId, Guid UserId, string DisplayName, string Role, string Status,
    IReadOnlyList<string>? CustomPermissions, IReadOnlyList<string> EffectivePermissions, DateTime JoinedAtUtc);

/// <summary>Invite someone by email. <paramref name="CustomPermissions"/> null = use the role template.</summary>
public sealed record InviteRequest(string Email, string DisplayName, string Role, IReadOnlyList<string>? CustomPermissions = null);

/// <summary>
/// The token is shown once. In v1 the inviter passes it on (email delivery comes later);
/// the invitee accepts it while signed in with the invited email.
/// </summary>
public sealed record InviteResponse(Guid InvitationId, string Token, DateTime ExpiresAtUtc);

public sealed record InvitationDto(Guid InvitationId, string Email, string DisplayName, string Role, DateTime ExpiresAtUtc);

public sealed record AcceptInvitationRequest(string Token);

public sealed record UpdateMemberRequest(string Role, IReadOnlyList<string>? CustomPermissions = null);
