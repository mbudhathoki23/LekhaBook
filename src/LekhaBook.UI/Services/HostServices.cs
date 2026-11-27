using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

// The shared UI talks to the device or server only through these interfaces
// (AGENT_PLAN § 3.2). Each host registers its own implementations:
// LekhaBook.App (MAUI, offline-first) and LekhaBook.Web (browser, online-only).
// IAttachmentService and IShareService are added in phase 7.

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
    LocalOnly,
}

public sealed record SyncIssueDto(long Id, string EntityType, Guid EntityId, string Operation, int Attempts, string Error, DateTime CreatedAtUtc);

/// <summary>Background sync between the device database and the server.</summary>
public interface ISyncService
{
    SyncState State { get; }

    /// <summary>Local changes not yet sent to the server.</summary>
    int PendingCount { get; }

    /// <summary>Changes the server refused, waiting for the user.</summary>
    int ProblemCount => 0;

    string? LastError => null;

    DateTime? LastSyncedAtUtc => null;

    event EventHandler? StateChanged;

    /// <summary>Raised when sync pulls new data from the server so open screens can refresh.</summary>
    event EventHandler? DataChanged { add { } remove { } }

    Task SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Notifies the sync engine that a local write occurred and should be pushed soon.</summary>
    void NotifyLocalWrite() { }

    Task<IReadOnlyList<SyncIssueDto>> GetIssuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncIssueDto>>([]);

    Task DiscardIssueAsync(long issueId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task RetryIssueAsync(long issueId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    /// <summary>A saved native session is waiting for the phone's device unlock.</summary>
    bool IsLocked => false;

    /// <summary>True when signed in from cached data because the server couldn't be reached.</summary>
    bool IsOffline { get; }

    /// <summary>True when running in Offline Local Mode on this device without an online cloud account.</summary>
    bool IsLocalMode => false;

    /// <summary>True on hosts that support standalone Offline Local Mode (MAUI Android/iOS/Windows).</summary>
    bool SupportsLocalMode => false;

    MeResponse? Me { get; }

    CompanySummary? CurrentCompany { get; }

    string? UserName { get; }

    string? CompanyName { get; }

    /// <summary>Raised when sign-in state, the user's companies or the current company change.</summary>
    event EventHandler? Changed;

    /// <summary>Restores a saved session. Safe to call more than once.</summary>
    Task InitializeAsync();

    /// <summary>Enters standalone Offline Local Mode on this device, creating an initial business if none exist yet.</summary>
    Task<SignInOutcome> StartLocalModeAsync(string businessName, string calendar = "BS") =>
        Task.FromResult(new SignInOutcome("Offline local mode is available in the installed app."));

    /// <summary>
    /// Signs in. On success the caller should go to <paramref name="returnUrl"/> unless
    /// <see cref="SignInOutcome.HostNavigated"/> (the web host reloads the page to set its cookie).
    /// </summary>
    Task<SignInOutcome> SignInAsync(string email, string password, string? returnUrl,
        string? twoFactorCode = null, string? recoveryCode = null);

    Task<SignInOutcome> UnlockAsync() => Task.FromResult(new SignInOutcome("Device unlock is unavailable."));

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
public sealed record SignInOutcome(string? Error, bool HostNavigated = false, bool RequiresTwoFactor = false)
{
    public bool Ok => Error is null && !RequiresTwoFactor;
}

/// <summary>Result of an API call: a value, or a message to show.</summary>
public sealed record ApiResult<T>(T? Value, string? Error, int StatusCode)
{
    public bool Ok => Error is null;

    public DuplicateTransactionWarning? DuplicateWarning { get; init; }

    /// <summary>The server couldn't be reached (offline, DNS, timeout).</summary>
    public bool IsNetworkError => StatusCode == 0;

    public bool IsForbidden => StatusCode == 403;
}

/// <summary>
/// Books data for the current company: parties, money accounts and transactions.
/// Until device sync lands (phase 8) both hosts use the server (D14); the MAUI app then switches to its local
/// database plus outbox. New rows get their id on the client, so saving them again never duplicates them.
/// </summary>
public interface IDataService
{
    Task<ApiResult<List<PartyDto>>> GetPartiesAsync(Guid companyId);

    Task<ApiResult<PartyDto>> GetPartyAsync(Guid companyId, Guid partyId);

    /// <summary>Adds a party; <see cref="SavePartyRequest.Id"/> should be set (a new UUIDv7).</summary>
    Task<ApiResult<PartyDto>> AddPartyAsync(Guid companyId, SavePartyRequest request);

    Task<ApiResult<PartyDto>> UpdatePartyAsync(Guid companyId, Guid partyId, SavePartyRequest request);

    Task<ApiResult<bool>> DeletePartyAsync(Guid companyId, Guid partyId);

    Task<ApiResult<LedgerDto>> GetPartyLedgerAsync(Guid companyId, Guid partyId);

    Task<ApiResult<List<PartyCategoryDto>>> GetPartyCategoriesAsync(Guid companyId);

    Task<ApiResult<PartyCategoryDto>> AddPartyCategoryAsync(Guid companyId, SavePartyCategoryRequest request);

    Task<ApiResult<List<MoneyAccountDto>>> GetMoneyAccountsAsync(Guid companyId);

    Task<ApiResult<MoneyAccountDto>> GetMoneyAccountAsync(Guid companyId, Guid accountId);

    Task<ApiResult<MoneyAccountDto>> AddMoneyAccountAsync(Guid companyId, SaveMoneyAccountRequest request);

    Task<ApiResult<MoneyAccountDto>> UpdateMoneyAccountAsync(Guid companyId, Guid accountId, SaveMoneyAccountRequest request);

    Task<ApiResult<bool>> DeleteMoneyAccountAsync(Guid companyId, Guid accountId);

    Task<ApiResult<LedgerDto>> GetAccountLedgerAsync(Guid companyId, Guid accountId);

    /// <summary>Income and expense heads.</summary>
    Task<ApiResult<List<CategoryDto>>> GetCategoriesAsync(Guid companyId);

    /// <summary>Posted entries the user may see, newest first.</summary>
    Task<ApiResult<List<TransactionDto>>> GetTransactionsAsync(Guid companyId);

    Task<ApiResult<TransactionDto>> GetTransactionAsync(Guid companyId, Guid transactionId);

    /// <summary>Adds an entry; <see cref="CreateTransactionRequest.Id"/> should be set (a new UUIDv7).</summary>
    Task<ApiResult<TransactionDto>> AddTransactionAsync(Guid companyId, CreateTransactionRequest request);

    Task<ApiResult<TransactionDto>> UpdateTransactionAsync(Guid companyId, Guid transactionId, UpdateTransactionRequest request);

    /// <summary>Voids the entry.</summary>
    Task<ApiResult<bool>> DeleteTransactionAsync(Guid companyId, Guid transactionId);
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

    /// <summary>Company details, including whether a logo is set.</summary>
    Task<ApiResult<CompanyDetails>> GetCompanyAsync(Guid companyId);

    /// <summary>The company logo's bytes; an error with StatusCode 404 when there is none.</summary>
    Task<ApiResult<AttachmentFile>> GetLogoAsync(Guid companyId);

    /// <summary>Sets the company logo (JPEG, PNG or WebP, up to 2 MB). Replaces any existing logo.</summary>
    Task<ApiResult<bool>> UploadLogoAsync(Guid companyId, Stream fileStream, string fileName, string contentType);

    Task<ApiResult<bool>> DeleteLogoAsync(Guid companyId);

    /// <summary>Sets how the company's dates are shown and typed: "BS" or "AD". Stored dates don't change.</summary>
    Task<ApiResult<bool>> UpdateCalendarAsync(Guid companyId, string calendar);
}

/// <summary>Attachments: capture, pick, store, get (AGENT_PLAN § 7).</summary>
public interface IAttachmentService
{
    /// <summary>Capture a photo from camera (MAUI only).</summary>
    Task<ApiResult<AttachmentDto>> CapturePhotoAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default);

    /// <summary>Pick a photo from gallery / choose a file (MAUI / Browser).</summary>
    Task<ApiResult<AttachmentDto>> PickAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default);

    /// <summary>Upload an attachment file (multipart) and create Attachment row.</summary>
    Task<ApiResult<AttachmentDto>> UploadAttachmentAsync(Guid companyId, Stream fileStream, string fileName, string contentType, Guid? transactionId = null, CancellationToken ct = default);

    /// <summary>Get attachments for a transaction or company.</summary>
    Task<ApiResult<List<AttachmentDto>>> GetAttachmentsAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default);

    /// <summary>Delete an attachment (soft-delete via IsDeleted; syncs like edits).</summary>
    Task<ApiResult<bool>> DeleteAttachmentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default);

    /// <summary>The file itself, for showing an image or opening a PDF.</summary>
    Task<ApiResult<AttachmentFile>> GetContentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default);
}

/// <summary>Share a PDF statement or other file (AGENT_PLAN § 7).</summary>
public interface IShareService
{
    /// <summary>Share a file via platform share target (MAUI) or navigator.share (Browser).</summary>
    Task<ApiResult<bool>> ShareAsync(string title, string text, Stream? fileStream = null, string? fileName = null, CancellationToken ct = default);
}

public sealed record OfflineLocalSummary(int Companies, int Parties, int MoneyAccounts, int Transactions, int Attachments);

public sealed record UpgradeResult(
    bool Ok,
    string? Error = null,
    bool RequiresTwoFactor = false,
    int CompaniesMigrated = 0,
    int TransactionsMigrated = 0,
    int AttachmentsUploaded = 0);

/// <summary>Upgrades a device's Offline Local Mode books into a synced Online Cloud account.</summary>
public interface IOfflineUpgradeService
{
    bool IsAvailable { get; }

    Task<OfflineLocalSummary> GetLocalSummaryAsync(CancellationToken ct = default);

    Task<UpgradeResult> UpgradeWithNewAccountAsync(
        string displayName, string email, string password, Action<string>? onProgress = null, CancellationToken ct = default);

    Task<UpgradeResult> UpgradeWithExistingAccountAsync(
        string email, string password, string? twoFactorCode = null, string? recoveryCode = null,
        Action<string>? onProgress = null, CancellationToken ct = default);
}

/// <summary>Portable JSON backup &amp; restore for offline/local companies.</summary>
public interface IBackupService
{
    bool IsAvailable { get; }

    Task<ApiResult<string>> ExportCompanyJsonAsync(Guid companyId, CancellationToken ct = default);

    Task<ApiResult<CompanySummary>> ImportCompanyJsonAsync(string json, CancellationToken ct = default);
}

/// <summary>Light/dark theme preference stored per device.</summary>
public interface IThemeService
{
    bool IsDarkMode { get; }

    event EventHandler? Changed;

    Task InitializeAsync();

    Task SetDarkModeAsync(bool dark);
}
