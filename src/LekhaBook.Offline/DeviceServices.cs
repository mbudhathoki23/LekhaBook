using LekhaBook.Application;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Offline;

/// <summary>Bridges Offline Local Mode (no online account) into SessionService.</summary>
public sealed class DeviceLocalSessionProvider(LocalBooksStore store) : ILocalSessionProvider
{
    public Guid LocalUserId => UserDatabaseFactory.LocalOfflineUserId;

    public async Task<IReadOnlyList<CompanySummary>> ListCompaniesAsync() => await store.ListCompaniesAsync();

    public async Task<CompanySummary> CreateCompanyAsync(CreateCompanyRequest request)
    {
        var result = await store.CreateLocalCompanyAsync(request, LocalUserId);
        return result.Value ?? throw new InvalidOperationException(result.Error ?? "Could not create local company.");
    }
}

/// <summary>
/// Offline-first IDataService for MAUI: reads and writes local SQLite immediately so every screen
/// works with zero latency in both Offline Local Mode and Online-Synced Mode (including airplane mode).
/// </summary>
public sealed class SqliteDataService(
    LocalBooksStore store,
    ISessionService session,
    ISyncService sync,
    TimeProvider? clock = null) : IDataService
{
    private readonly DeviceRules _rules = new(session, store, clock ?? TimeProvider.System);

    private static ApiResult<T> Refused<T>(string why) => new(default, why, 403);

    private const string NoAccounts = "You don't have permission to view accounts.";

    private Guid CurrentUserId => session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;

    private bool EnqueueOutbox => !session.IsLocalMode;

    private HashSet<PartyType> VisiblePartyTypes()
    {
        var set = new HashSet<PartyType>();
        if (session.IsLocalMode || session.Can(Permission.CustomersView)) set.Add(PartyType.Customer);
        if (session.IsLocalMode || session.Can(Permission.SuppliersView)) set.Add(PartyType.Supplier);
        return set;
    }

    private HashSet<TransactionType> VisibleTransactionTypes()
    {
        var set = new HashSet<TransactionType>();
        foreach (var type in Enum.GetValues<TransactionType>())
        {
            if (session.IsLocalMode || session.Can(EntryRules.ViewPermissionFor(type)))
            {
                set.Add(type);
            }
        }
        return set;
    }

    private static ApiResult<T> ToApi<T>(LocalResult<T> r) =>
        new(r.Value, r.Error, r.StatusCode) { DuplicateWarning = r.DuplicateWarning };

    private ApiResult<T> NotifyAndReturn<T>(LocalResult<T> r)
    {
        if (r.Ok && EnqueueOutbox)
        {
            sync.NotifyLocalWrite();
        }
        return ToApi(r);
    }

    public async Task<ApiResult<List<PartyDto>>> GetPartiesAsync(Guid companyId) =>
        ToApi(await store.GetPartiesAsync(companyId, VisiblePartyTypes()));

    public async Task<ApiResult<PartyDto>> GetPartyAsync(Guid companyId, Guid partyId) =>
        ToApi(await store.GetPartyAsync(companyId, partyId, VisiblePartyTypes()));

    public async Task<ApiResult<PartyDto>> AddPartyAsync(Guid companyId, SavePartyRequest request)
    {
        var denied = await _rules.AddPartyAsync(companyId, request);
        return denied is null
            ? NotifyAndReturn(await store.AddPartyAsync(companyId, request, CurrentUserId, EnqueueOutbox))
            : Refused<PartyDto>(denied);
    }

    public async Task<ApiResult<PartyDto>> UpdatePartyAsync(Guid companyId, Guid partyId, SavePartyRequest request)
    {
        var denied = await _rules.EditPartyAsync(companyId, partyId, request);
        return denied is null
            ? NotifyAndReturn(await store.UpdatePartyAsync(companyId, partyId, request, CurrentUserId, EnqueueOutbox))
            : Refused<PartyDto>(denied);
    }

    public async Task<ApiResult<bool>> DeletePartyAsync(Guid companyId, Guid partyId)
    {
        var denied = await _rules.DeletePartyAsync(companyId, partyId);
        return denied is null
            ? NotifyAndReturn(await store.DeletePartyAsync(companyId, partyId, CurrentUserId, EnqueueOutbox))
            : Refused<bool>(denied);
    }

    public async Task<ApiResult<LedgerDto>> GetPartyLedgerAsync(Guid companyId, Guid partyId) =>
        ToApi(await store.GetPartyLedgerAsync(companyId, partyId, VisiblePartyTypes(), VisibleTransactionTypes()));

    public async Task<ApiResult<List<PartyCategoryDto>>> GetPartyCategoriesAsync(Guid companyId) =>
        ToApi(await store.GetPartyCategoriesAsync(companyId));

    public async Task<ApiResult<PartyCategoryDto>> AddPartyCategoryAsync(Guid companyId, SavePartyCategoryRequest request)
    {
        var denied = _rules.Has(companyId, Permission.SettingsFeatures, "You don't have permission to add party categories.");
        return denied is null
            ? NotifyAndReturn(await store.AddPartyCategoryAsync(companyId, request, CurrentUserId, EnqueueOutbox))
            : Refused<PartyCategoryDto>(denied);
    }

    public async Task<ApiResult<List<MoneyAccountDto>>> GetMoneyAccountsAsync(Guid companyId)
    {
        var denied = _rules.Has(companyId, Permission.AccountsView, NoAccounts);
        return denied is null
            ? ToApi(await store.GetMoneyAccountsAsync(companyId))
            : Refused<List<MoneyAccountDto>>(denied);
    }

    public async Task<ApiResult<MoneyAccountDto>> GetMoneyAccountAsync(Guid companyId, Guid accountId)
    {
        var denied = _rules.Has(companyId, Permission.AccountsView, NoAccounts);
        return denied is null
            ? ToApi(await store.GetMoneyAccountAsync(companyId, accountId))
            : Refused<MoneyAccountDto>(denied);
    }

    public async Task<ApiResult<MoneyAccountDto>> AddMoneyAccountAsync(Guid companyId, SaveMoneyAccountRequest request)
    {
        var denied = await _rules.AddMoneyAccountAsync(companyId, request);
        return denied is null
            ? NotifyAndReturn(await store.AddMoneyAccountAsync(companyId, request, CurrentUserId, EnqueueOutbox))
            : Refused<MoneyAccountDto>(denied);
    }

    public async Task<ApiResult<MoneyAccountDto>> UpdateMoneyAccountAsync(Guid companyId, Guid accountId, SaveMoneyAccountRequest request)
    {
        var denied = await _rules.EditMoneyAccountAsync(companyId, accountId, request);
        return denied is null
            ? NotifyAndReturn(await store.UpdateMoneyAccountAsync(companyId, accountId, request, CurrentUserId, EnqueueOutbox))
            : Refused<MoneyAccountDto>(denied);
    }

    public async Task<ApiResult<bool>> DeleteMoneyAccountAsync(Guid companyId, Guid accountId)
    {
        var denied = _rules.Has(companyId, Permission.AccountsDelete, "You don't have permission to delete accounts.");
        return denied is null
            ? NotifyAndReturn(await store.DeleteMoneyAccountAsync(companyId, accountId, CurrentUserId, EnqueueOutbox))
            : Refused<bool>(denied);
    }

    public async Task<ApiResult<LedgerDto>> GetAccountLedgerAsync(Guid companyId, Guid accountId)
    {
        var denied = _rules.Has(companyId, Permission.AccountsView, NoAccounts);
        return denied is null
            ? ToApi(await store.GetAccountLedgerAsync(companyId, accountId, VisibleTransactionTypes()))
            : Refused<LedgerDto>(denied);
    }

    public async Task<ApiResult<List<CategoryDto>>> GetCategoriesAsync(Guid companyId)
    {
        var denied = _rules.Has(companyId, Permission.IncomeExpenseView, "You don't have permission to view income and expense heads.");
        return denied is null
            ? ToApi(await store.GetCategoriesAsync(companyId))
            : Refused<List<CategoryDto>>(denied);
    }

    public async Task<ApiResult<List<TransactionDto>>> GetTransactionsAsync(Guid companyId) =>
        ToApi(await store.GetTransactionsAsync(companyId, VisibleTransactionTypes()));

    public async Task<ApiResult<TransactionDto>> GetTransactionAsync(Guid companyId, Guid transactionId) =>
        ToApi(await store.GetTransactionAsync(companyId, transactionId, VisibleTransactionTypes()));

    public async Task<ApiResult<TransactionDto>> AddTransactionAsync(Guid companyId, CreateTransactionRequest request)
    {
        var denied = await _rules.AddEntryAsync(companyId, MasterValidation.ParseTransactionType(request.Type), request.Date);
        return denied is null
            ? NotifyAndReturn(await store.AddTransactionAsync(companyId, request, CurrentUserId, EnqueueOutbox, VisibleTransactionTypes()))
            : Refused<TransactionDto>(denied);
    }

    public async Task<ApiResult<TransactionDto>> UpdateTransactionAsync(Guid companyId, Guid transactionId, UpdateTransactionRequest request)
    {
        var denied = await _rules.ChangeEntryAsync(companyId, transactionId, EntryAction.Edit, request.Date);
        return denied is null
            ? NotifyAndReturn(await store.UpdateTransactionAsync(companyId, transactionId, request, CurrentUserId, EnqueueOutbox, VisibleTransactionTypes()))
            : Refused<TransactionDto>(denied);
    }

    public async Task<ApiResult<bool>> DeleteTransactionAsync(Guid companyId, Guid transactionId)
    {
        var denied = await _rules.ChangeEntryAsync(companyId, transactionId, EntryAction.Delete);
        return denied is null
            ? NotifyAndReturn(await store.VoidTransactionAsync(companyId, transactionId, CurrentUserId, EnqueueOutbox))
            : Refused<bool>(denied);
    }
}

/// <summary>Offline-first balance adjustments and income/expense categories for MAUI.</summary>
public sealed class SqliteBookExtrasService(
    LocalBooksStore store,
    ISessionService session,
    ISyncService sync,
    TimeProvider? clock = null) : IBookExtrasApi
{
    private readonly DeviceRules _rules = new(session, store, clock ?? TimeProvider.System);

    private Guid CurrentUserId => session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;
    private bool EnqueueOutbox => !session.IsLocalMode;

    public async Task<ApiResult<bool>> AdjustBalanceAsync(Guid companyId, Guid accountId, AdjustBalanceRequest request)
    {
        var account = (await store.GetMoneyAccountAsync(companyId, accountId)).Value;
        var type = account is null
            ? request.Increase ? TransactionType.PartyAdjustAdd : TransactionType.PartyAdjustReduce
            : request.Increase ? TransactionType.AddMoney : TransactionType.ReduceMoney;
        if (await _rules.AddEntryAsync(companyId, type, request.Date) is { } denied)
        {
            return new(false, denied, 403);
        }

        var r = await store.AdjustBalanceAsync(companyId, accountId, request, CurrentUserId, EnqueueOutbox);
        if (r.Ok && EnqueueOutbox) sync.NotifyLocalWrite();
        return new(r.Value, r.Error, r.StatusCode);
    }

    public async Task<ApiResult<CategoryDto>> AddCategoryAsync(Guid companyId, SaveCategoryRequest request)
    {
        if (_rules.Has(companyId, Permission.SettingsFeatures, "You don't have permission to add categories.") is { } denied)
        {
            return new(null, denied, 403);
        }

        var r = await store.AddCategoryAsync(companyId, request, CurrentUserId, EnqueueOutbox);
        if (r.Ok && EnqueueOutbox) sync.NotifyLocalWrite();
        return new(r.Value, r.Error, r.StatusCode);
    }
}

/// <summary>
/// Company management for MAUI: runs 100% locally in Offline Local Mode, and uses the API with local
/// SQLite metadata/logo caching in Online-Synced Mode so company headers and logos work offline too.
/// </summary>
public sealed class HybridCompanyService(
    LocalBooksStore store,
    CompanyApi remote,
    ISessionService session,
    IConnectivityService connectivity,
    ISyncService sync) : ICompanyApi
{
    private const string OnlineOnlyStaffMessage = "Staff and invitations require Online Cloud mode. Upgrade in Settings to add your team.";

    private Guid CurrentUserId => session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;

    public async Task<ApiResult<CompanySummary>> CreateCompanyAsync(CreateCompanyRequest request)
    {
        if (session.IsLocalMode)
        {
            var local = await store.CreateLocalCompanyAsync(request, CurrentUserId);
            return new(local.Value, local.Error, local.StatusCode);
        }

        var created = await remote.CreateCompanyAsync(request);
        if (created.Ok && created.Value is not null)
        {
            // Pull the newly created company's seeded accounts right away.
            _ = sync.SyncNowAsync();
        }
        return created;
    }

    public async Task<ApiResult<CompanyDetails>> GetCompanyAsync(Guid companyId)
    {
        if (session.IsLocalMode)
        {
            var local = await store.GetCompanyDetailsAsync(companyId);
            return new(local.Value, local.Error, local.StatusCode);
        }

        if (connectivity.IsOnline)
        {
            var r = await remote.GetCompanyAsync(companyId);
            if (r.Ok && r.Value is not null)
            {
                await store.EnsureCompanyMetadataAsync(r.Value);
                return r;
            }
        }

        var cached = await store.GetCompanyDetailsAsync(companyId);
        return new(cached.Value, cached.Error, cached.StatusCode);
    }

    public async Task<ApiResult<bool>> UpdateCalendarAsync(Guid companyId, string calendar)
    {
        if (session.IsLocalMode)
        {
            var local = await store.UpdateCalendarAsync(companyId, calendar);
            return new(local.Value, local.Error, local.StatusCode);
        }

        var r = await remote.UpdateCalendarAsync(companyId, calendar);
        if (r.Ok)
        {
            await store.UpdateCalendarAsync(companyId, calendar);
        }
        return r;
    }

    public async Task<ApiResult<AttachmentFile>> GetLogoAsync(Guid companyId)
    {
        var local = await store.GetLocalLogoAsync(companyId);
        if (local.Ok)
        {
            return new(local.Value, null, 200);
        }

        if (session.IsLocalMode || !connectivity.IsOnline)
        {
            return new(null, "Not found.", 404);
        }

        var remoteLogo = await remote.GetLogoAsync(companyId);
        if (remoteLogo.Ok && remoteLogo.Value is { } file)
        {
            await store.SaveLocalLogoAsync(companyId, file.Content, file.ContentType);
        }
        return remoteLogo;
    }

    public async Task<ApiResult<bool>> UploadLogoAsync(Guid companyId, Stream fileStream, string fileName, string contentType)
    {
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms);
        var bytes = ms.ToArray();

        if (session.IsLocalMode)
        {
            var local = await store.SaveLocalLogoAsync(companyId, bytes, contentType);
            return new(local.Value, local.Error, local.StatusCode);
        }

        using var uploadStream = new MemoryStream(bytes);
        var r = await remote.UploadLogoAsync(companyId, uploadStream, fileName, contentType);
        if (r.Ok)
        {
            await store.SaveLocalLogoAsync(companyId, bytes, contentType);
        }
        return r;
    }

    public async Task<ApiResult<bool>> DeleteLogoAsync(Guid companyId)
    {
        if (session.IsLocalMode)
        {
            var local = await store.DeleteLocalLogoAsync(companyId);
            return new(local.Value, local.Error, local.StatusCode);
        }

        var r = await remote.DeleteLogoAsync(companyId);
        if (r.Ok)
        {
            await store.DeleteLocalLogoAsync(companyId);
        }
        return r;
    }

    public Task<ApiResult<List<MemberDto>>> GetMembersAsync(Guid companyId) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<List<MemberDto>>(null, OnlineOnlyStaffMessage, 403))
            : remote.GetMembersAsync(companyId);

    public Task<ApiResult<MemberDto>> UpdateMemberAsync(Guid companyId, Guid memberId, UpdateMemberRequest request) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<MemberDto>(null, OnlineOnlyStaffMessage, 403))
            : remote.UpdateMemberAsync(companyId, memberId, request);

    public Task<ApiResult<bool>> RevokeMemberAsync(Guid companyId, Guid memberId) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<bool>(false, OnlineOnlyStaffMessage, 403))
            : remote.RevokeMemberAsync(companyId, memberId);

    public Task<ApiResult<List<InvitationDto>>> GetInvitationsAsync(Guid companyId) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<List<InvitationDto>>(null, OnlineOnlyStaffMessage, 403))
            : remote.GetInvitationsAsync(companyId);

    public Task<ApiResult<InviteResponse>> InviteAsync(Guid companyId, InviteRequest request) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<InviteResponse>(null, OnlineOnlyStaffMessage, 403))
            : remote.InviteAsync(companyId, request);

    public Task<ApiResult<bool>> CancelInvitationAsync(Guid companyId, Guid invitationId) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<bool>(false, OnlineOnlyStaffMessage, 403))
            : remote.CancelInvitationAsync(companyId, invitationId);

    public Task<ApiResult<CompanySummary>> AcceptInvitationAsync(string token) =>
        session.IsLocalMode
            ? Task.FromResult(new ApiResult<CompanySummary>(null, OnlineOnlyStaffMessage, 403))
            : remote.AcceptInvitationAsync(token);
}

/// <summary>
/// Offline-first attachments for MAUI: saves to device storage immediately and queues upload
/// via DeviceSyncEngine in Online-Synced Mode; reads from local disk first with online fallback + caching.
/// </summary>
public sealed class SqliteAttachmentService(
    LocalBooksStore store,
    ApiAttachmentService remote,
    ISessionService session,
    IConnectivityService connectivity,
    ISyncService sync,
    TimeProvider? clock = null) : IAttachmentService
{
    private readonly DeviceRules _rules = new(session, store, clock ?? TimeProvider.System);

    private Guid CurrentUserId => session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;
    private bool PendingUpload => !session.IsLocalMode;

    public Task<ApiResult<AttachmentDto>> CapturePhotoAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<AttachmentDto>(null, "Use IDocumentCapture + UploadAttachmentAsync.", 0));

    public Task<ApiResult<AttachmentDto>> PickAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<AttachmentDto>(null, "Use IDocumentCapture + UploadAttachmentAsync.", 0));

    public async Task<ApiResult<AttachmentDto>> UploadAttachmentAsync(
        Guid companyId, Stream fileStream, string fileName, string contentType, Guid? transactionId = null, CancellationToken ct = default)
    {
        if (transactionId is not { } tid)
        {
            return new(null, "A transaction is required for attachments.", 400);
        }

        // Adding or removing proof changes the entry, so it needs the same right as editing it.
        if (await _rules.ChangeEntryAsync(companyId, tid, EntryAction.Edit) is { } denied)
        {
            return new(null, denied, 403);
        }

        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        var saved = await store.SaveLocalAttachmentAsync(companyId, tid, fileName, contentType, ms.ToArray(), CurrentUserId, PendingUpload, ct);
        if (saved.Ok && PendingUpload)
        {
            sync.NotifyLocalWrite();
        }
        return new(saved.Value, saved.Error, saved.StatusCode);
    }

    public async Task<ApiResult<List<AttachmentDto>>> GetAttachmentsAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default)
    {
        var r = await store.GetAttachmentsAsync(companyId, transactionId, ct);
        return new(r.Value, r.Error, r.StatusCode);
    }

    public async Task<ApiResult<bool>> DeleteAttachmentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default)
    {
        var entryId = (await store.GetAttachmentsAsync(companyId, null, ct)).Value?
            .FirstOrDefault(a => a.Id == attachmentId)?.TransactionId;
        if (entryId is { } tid && await _rules.ChangeEntryAsync(companyId, tid, EntryAction.Edit) is { } denied)
        {
            return new(false, denied, 403);
        }

        var r = await store.DeleteAttachmentAsync(companyId, attachmentId, CurrentUserId, enqueueOutbox: !session.IsLocalMode, ct);
        if (r.Ok && !session.IsLocalMode)
        {
            sync.NotifyLocalWrite();
        }
        return new(r.Value, r.Error, r.StatusCode);
    }

    public async Task<ApiResult<AttachmentFile>> GetContentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default)
    {
        if (await store.TryReadLocalAttachmentFileAsync(companyId, attachmentId, ct) is { } localFile)
        {
            return new(localFile, null, 200);
        }

        if (session.IsLocalMode || !connectivity.IsOnline)
        {
            return new(null, "This file is not cached offline yet. Connect to the internet to download it.", 0);
        }

        var downloaded = await remote.GetContentAsync(companyId, attachmentId, ct);
        if (downloaded.Ok && downloaded.Value is { } file)
        {
            await store.CacheDownloadedAttachmentAsync(companyId, attachmentId, file.FileName, file.Content, ct);
        }
        return downloaded;
    }
}

/// <summary>Portable JSON backup &amp; restore for the local SQLite database.</summary>
public sealed class LocalBackupService(LocalBooksStore store, ISessionService session) : IBackupService
{
    public bool IsAvailable => true;

    public async Task<ApiResult<string>> ExportCompanyJsonAsync(Guid companyId, CancellationToken ct = default)
    {
        var r = await store.ExportCompanyBackupJsonAsync(companyId, ct);
        return new(r.Value, r.Error, r.StatusCode);
    }

    public async Task<ApiResult<CompanySummary>> ImportCompanyJsonAsync(string json, CancellationToken ct = default)
    {
        if (!session.IsLocalMode)
        {
            return new(null, "JSON restore is supported in Offline Local Mode. In Online Mode, use Import Data.", 400);
        }

        var userId = session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;
        var r = await store.ImportCompanyBackupJsonAsync(json, userId, ct);
        if (r.Ok && r.Value is not null)
        {
            await session.RefreshAsync();
            await session.SelectCompanyAsync(r.Value.CompanyId);
        }
        return new(r.Value, r.Error, r.StatusCode);
    }
}

/// <summary>
/// Upgrades a device's Offline Local Mode data into a synced Online Cloud account:
/// authenticates, migrates all local companies atomically &amp; idempotently, uploads logos &amp; attachments,
/// then switches to Online-Synced Mode and pulls the confirmed server state.
/// </summary>
public sealed class OfflineUpgradeService(
    UserDatabaseFactory databases,
    string filesRoot,
    ISessionService session,
    CompanyApi companyApi,
    ApiAttachmentService remoteAttachments,
    ISyncService sync) : IOfflineUpgradeService
{
    public bool IsAvailable => true;

    private LocalBooksStore CreateLocalOfflineStore() =>
        new(new FixedUserDbFactory(databases, UserDatabaseFactory.LocalOfflineUserId), filesRoot);

    public async Task<OfflineLocalSummary> GetLocalSummaryAsync(CancellationToken ct = default)
    {
        if (!databases.DatabaseExists(UserDatabaseFactory.LocalOfflineUserId))
        {
            return new OfflineLocalSummary(0, 0, 0, 0, 0);
        }

        var summary = await CreateLocalOfflineStore().GetSummaryAsync(ct);
        return new OfflineLocalSummary(summary.Companies, summary.Parties, summary.MoneyAccounts, summary.Transactions, summary.Attachments);
    }

    public async Task<UpgradeResult> UpgradeWithNewAccountAsync(
        string displayName, string email, string password, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var localStore = CreateLocalOfflineStore();
        onProgress?.Invoke("Reading your offline books…");
        var bundles = await localStore.BuildMigrationBundlesAsync(ct);

        onProgress?.Invoke("Creating your online account…");
        var reg = await session.RegisterAsync(displayName, email, password, returnUrl: null);
        if (!reg.Ok)
        {
            return new UpgradeResult(false, reg.Error);
        }

        return await UploadBundlesAndFinishAsync(bundles, onProgress, ct);
    }

    public async Task<UpgradeResult> UpgradeWithExistingAccountAsync(
        string email, string password, string? twoFactorCode = null, string? recoveryCode = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var localStore = CreateLocalOfflineStore();
        onProgress?.Invoke("Reading your offline books…");
        var bundles = await localStore.BuildMigrationBundlesAsync(ct);

        onProgress?.Invoke("Signing in to your online account…");
        var signIn = await session.SignInAsync(email, password, returnUrl: null, twoFactorCode, recoveryCode);
        if (signIn.RequiresTwoFactor)
        {
            return new UpgradeResult(false, RequiresTwoFactor: true);
        }

        if (!signIn.Ok)
        {
            return new UpgradeResult(false, signIn.Error);
        }

        return await UploadBundlesAndFinishAsync(bundles, onProgress, ct);
    }

    private async Task<UpgradeResult> UploadBundlesAndFinishAsync(
        List<LocalCompanyMigrationBundle> bundles, Action<string>? onProgress, CancellationToken ct)
    {
        var finished = false;
        try
        {
            var result = await UploadBundlesAsync(bundles, onProgress, ct);
            finished = result.Ok;
            return result;
        }
        finally
        {
            // Until everything is online, the books on this device stay the ones in use and the upgrade can be tried again.
            if (!finished)
            {
                await sync.WaitUntilIdleAsync(CancellationToken.None);
                await session.ReturnToLocalModeAsync();
            }
        }
    }

    private async Task<UpgradeResult> UploadBundlesAsync(
        List<LocalCompanyMigrationBundle> bundles, Action<string>? onProgress, CancellationToken ct)
    {
        int companiesMigrated = 0;
        int transactionsMigrated = 0;
        int attachmentsUploaded = 0;
        int failedFiles = 0;
        Guid? firstCompanyId = null;

        for (var i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            onProgress?.Invoke($"Uploading business {i + 1} of {bundles.Count}: {bundle.Snapshot.Name}…");
            var res = await companyApi.MigrateOfflineCompanyAsync(bundle.Snapshot);
            if (!res.Ok || res.Value is null)
            {
                return new UpgradeResult(false,
                    $"Could not upload '{bundle.Snapshot.Name}': {res.Error ?? "Unknown error"}. You can retry safely — already uploaded businesses will not duplicate.",
                    CompaniesMigrated: companiesMigrated,
                    TransactionsMigrated: transactionsMigrated,
                    AttachmentsUploaded: attachmentsUploaded);
            }

            firstCompanyId ??= res.Value.Company.CompanyId;
            companiesMigrated++;
            transactionsMigrated += res.Value.TransactionsMigrated;

            // Upload company logo if present
            if (bundle.LogoPath is not null && bundle.LogoContentType is not null && File.Exists(bundle.LogoPath))
            {
                onProgress?.Invoke($"Uploading logo for {bundle.Snapshot.Name}…");
                await using var logoStream = File.OpenRead(bundle.LogoPath);
                var logo = await companyApi.UploadLogoAsync(bundle.Snapshot.CompanyId, logoStream, Path.GetFileName(bundle.LogoPath), bundle.LogoContentType);
                failedFiles += logo.Ok ? 0 : 1;
            }

            // Upload local attachments for posted entries
            if (bundle.Attachments.Count > 0)
            {
                onProgress?.Invoke($"Uploading {bundle.Attachments.Count} attachment(s) for {bundle.Snapshot.Name}…");
                var taken = new HashSet<Guid>();
                foreach (var att in bundle.Attachments)
                {
                    if (!File.Exists(att.LocalPath)) continue;
                    // A retry must not add the same file twice.
                    var size = new FileInfo(att.LocalPath).Length;
                    var there = await remoteAttachments.GetAttachmentsAsync(bundle.Snapshot.CompanyId, att.TransactionId, ct);
                    var same = there.Value?.FirstOrDefault(a => !taken.Contains(a.Id) && a.FileName == att.FileName && a.SizeBytes == size);
                    if (same is not null)
                    {
                        taken.Add(same.Id);
                        attachmentsUploaded++;
                        continue;
                    }

                    await using var fs = File.OpenRead(att.LocalPath);
                    var up = await remoteAttachments.UploadAttachmentAsync(
                        bundle.Snapshot.CompanyId, fs, att.FileName, att.ContentType, att.TransactionId, ct);
                    if (up.Ok && up.Value is not null)
                    {
                        taken.Add(up.Value.Id);
                        attachmentsUploaded++;
                    }
                    else
                    {
                        failedFiles++;
                    }
                }
            }
        }

        // Nothing is put away while something is still only on this device.
        if (failedFiles > 0)
        {
            return new UpgradeResult(false,
                $"{failedFiles} photo or logo file(s) could not be uploaded. Your offline books are kept on this device. " +
                "Try the upgrade again, signing in with the same account.",
                CompaniesMigrated: companiesMigrated,
                TransactionsMigrated: transactionsMigrated,
                AttachmentsUploaded: attachmentsUploaded);
        }

        onProgress?.Invoke("Syncing your cloud books to this device…");
        await session.RefreshAsync();
        if (firstCompanyId is { } cid)
        {
            await session.SelectCompanyAsync(cid);
        }

        await sync.SyncNowAsync(ct);

        // Archive the old offline-only DB file safely so nothing is ever lost, while preventing stale reuse.
        try
        {
            var localDbPath = databases.FilePathFor(UserDatabaseFactory.LocalOfflineUserId);
            if (File.Exists(localDbPath))
            {
                var archivePath = $"{localDbPath}.upgraded-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Move(localDbPath, archivePath, overwrite: true);
            }
        }
        catch (IOException)
        {
            // File may still be releasing a pooled SQLite handle; safe to leave in place.
        }

        onProgress?.Invoke("Upgrade complete! Your books are now synced in the cloud.");
        return new UpgradeResult(true,
            CompaniesMigrated: companiesMigrated,
            TransactionsMigrated: transactionsMigrated,
            AttachmentsUploaded: attachmentsUploaded);
    }

    private sealed class FixedUserDbFactory(UserDatabaseFactory inner, Guid userId) : IDbContextFactory<SqliteDbContext>
    {
        public SqliteDbContext CreateDbContext() => inner.CreateDbContextForUser(userId);
    }
}
