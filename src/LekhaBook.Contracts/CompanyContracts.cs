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

// Offline-to-online cloud upgrade: uploads a local offline company snapshot atomically and idempotently.

public sealed record MigratePartyCategoryDto(Guid Id, string Name, bool IsDeleted = false);

public sealed record MigrateAccountDto(
    Guid Id,
    string Name,
    string Type,
    bool IsSystem,
    bool IsActive,
    decimal OpeningBalance,
    string OpeningSide,
    DateOnly? OpeningAsOf,
    string? PartyType,
    string? Phone,
    string? Email,
    string? Address,
    string? Pan,
    Guid? PartyCategoryId,
    string? AccountHolderName,
    string? AccountNumber,
    bool IsDeleted = false);

public sealed record MigrateTransactionDto(
    Guid Id,
    string Type,
    string VoucherNo,
    bool IsManualNo,
    bool IsProvisionalNo,
    DateOnly Date,
    decimal Amount,
    Guid FromAccountId,
    Guid ToAccountId,
    string? Remarks,
    string? ChequeNo,
    DateOnly? ChequeDate,
    string Status,
    bool IsDeleted = false);

public sealed record MigrateOfflineCompanyRequest(
    Guid CompanyId,
    string Name,
    string? Phone,
    string? Address,
    string? Pan,
    string Calendar,
    int FiscalYearStartMonth,
    string Currency,
    string TimeZoneId,
    IReadOnlyList<MigratePartyCategoryDto> PartyCategories,
    IReadOnlyList<MigrateAccountDto> Accounts,
    IReadOnlyList<MigrateTransactionDto> Transactions);

public sealed record MigrateOfflineCompanyResponse(
    CompanySummary Company,
    int AccountsMigrated,
    int TransactionsMigrated,
    bool AlreadyExisted);
