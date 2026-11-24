using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

// The shared UI talks to the device or server only through these interfaces
// (AGENT_PLAN § 3.2). Each host registers its own implementations:
// LekhaBook.App (MAUI, offline-first) and LekhaBook.Web (browser, online-only).
// IDataService, IAttachmentService and IShareService are added in later phases.

/// <summary>Where the UI is running, so layouts can adapt.</summary>
public interface IPlatformInfo
{
    /// <summary>"Android", "iOS", "Windows" or "Web".</summary>
    string Platform { get; }

    /// <summary>"Phone", "Tablet", "Desktop" or "Web".</summary>
    string FormFactor { get; }
}

/// <summary>Network state of the device.</summary>
public interface IConnectivityService
{
    bool IsOnline { get; }

    event EventHandler<bool>? OnlineChanged;
}

public enum SyncState
{
    Synced,
    Pending,
    Syncing,
    Offline,
    Error,
}

/// <summary>Background sync between the device database and the server.</summary>
public interface ISyncService
{
    SyncState State { get; }

    /// <summary>Local changes not yet sent to the server.</summary>
    int PendingCount { get; }

    event EventHandler? StateChanged;

    Task SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The signed-in user and the company they are working in. Implemented once in LekhaBook.Client;
/// each host only supplies where tokens and small settings are kept.
/// </summary>
public interface ISessionService
{
    /// <summary>False until <see cref="InitializeAsync"/> has finished (show a loading state).</summary>
    bool IsInitialized { get; }

    bool IsSignedIn { get; }

    /// <summary>True when signed in from cached data because the server couldn't be reached.</summary>
    bool IsOffline { get; }

    MeResponse? Me { get; }

    CompanySummary? CurrentCompany { get; }

    string? UserName { get; }

    string? CompanyName { get; }

    /// <summary>Raised when sign-in state, the user's companies or the current company change.</summary>
    event EventHandler? Changed;

    /// <summary>Restores a saved session. Safe to call more than once.</summary>
    Task InitializeAsync();

    /// <summary>
    /// Signs in. On success the caller should go to <paramref name="returnUrl"/> unless
    /// <see cref="SignInOutcome.HostNavigated"/> (the web host reloads the page to set its cookie).
    /// </summary>
    Task<SignInOutcome> SignInAsync(string email, string password, string? returnUrl);

    /// <summary>Creates the account and signs in.</summary>
    Task<SignInOutcome> RegisterAsync(string displayName, string email, string password, string? returnUrl);

    Task SignOutAsync();

    Task SelectCompanyAsync(Guid companyId);

    /// <summary>Reloads the user's companies and permissions from the server.</summary>
    Task RefreshAsync();

    /// <summary>Whether the current user holds <paramref name="permission"/> in the current company.</summary>
    bool Can(string permission);
}

/// <summary>Error to show (null = success), and whether the host already navigated away.</summary>
public sealed record SignInOutcome(string? Error, bool HostNavigated = false)
{
    public bool Ok => Error is null;
}

/// <summary>Result of an API call: a value, or a message to show.</summary>
public sealed record ApiResult<T>(T? Value, string? Error, int StatusCode)
{
    public bool Ok => Error is null;

    /// <summary>The server couldn't be reached (offline, DNS, timeout).</summary>
    public bool IsNetworkError => StatusCode == 0;

    public bool IsForbidden => StatusCode == 403;
}

/// <summary>Company, staff and invitation calls (AGENT_PLAN § 9).</summary>
public interface ICompanyApi
{
    Task<ApiResult<CompanySummary>> CreateCompanyAsync(CreateCompanyRequest request);

    Task<ApiResult<List<MemberDto>>> GetMembersAsync(Guid companyId);

    Task<ApiResult<MemberDto>> UpdateMemberAsync(Guid companyId, Guid memberId, UpdateMemberRequest request);

    Task<ApiResult<bool>> RevokeMemberAsync(Guid companyId, Guid memberId);

    Task<ApiResult<List<InvitationDto>>> GetInvitationsAsync(Guid companyId);

    Task<ApiResult<InviteResponse>> InviteAsync(Guid companyId, InviteRequest request);

    Task<ApiResult<bool>> CancelInvitationAsync(Guid companyId, Guid invitationId);

    Task<ApiResult<CompanySummary>> AcceptInvitationAsync(string token);
}
