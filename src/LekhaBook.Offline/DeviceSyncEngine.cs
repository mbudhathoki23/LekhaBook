using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LekhaBook.Offline;

/// <summary>
/// Full offline-first sync engine for MAUI (phase 8):
/// - Debounced automatic push on local writes, connectivity restore, company switch, and 5-minute background ticks
/// - Pushes master data, transactions, adjustments, and deletes through /sync/push
/// - Uploads pending local attachments once their parent transactions have synced
/// - Pulls server changes cleanly into SQLite and raises DataChanged so open screens refresh
/// - Tracks conflicts/problems for user inspection &amp; resolution, and wipes company data on 403 revocation
/// </summary>
public sealed class DeviceSyncEngine : ISyncService, IDisposable
{
    private readonly IDbContextFactory<SqliteDbContext> _dbFactory;
    private readonly LocalBooksStore _localStore;
    private readonly ISessionService _session;
    private readonly IConnectivityService _connectivity;
    private readonly LekhaBookApi _api;
    private readonly CompanyApi _companyApi;
    private readonly ApiAttachmentService _remoteAttachments;
    private readonly ILogger<DeviceSyncEngine> _logger;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _periodicSyncCts = new();
    private CancellationTokenSource? _debounceCts;
    private Guid? _lastSyncedCompanyId;

    /// <summary>Records this run uploaded: their echo in the download is not news to the screens.</summary>
    private readonly HashSet<Guid> _sent = [];

    private SyncState _state = SyncState.Synced;
    private int _pendingCount;
    private int _problemCount;
    private string? _lastError;
    private DateTime? _lastSyncedAtUtc;

    public event EventHandler? StateChanged;
    public event EventHandler? DataChanged;

    public DeviceSyncEngine(
        IDbContextFactory<SqliteDbContext> dbFactory,
        LocalBooksStore localStore,
        ISessionService session,
        IConnectivityService connectivity,
        LekhaBookApi api,
        CompanyApi companyApi,
        ApiAttachmentService remoteAttachments,
        ILogger<DeviceSyncEngine> logger)
    {
        _dbFactory = dbFactory;
        _localStore = localStore;
        _session = session;
        _connectivity = connectivity;
        _api = api;
        _companyApi = companyApi;
        _remoteAttachments = remoteAttachments;
        _logger = logger;

        connectivity.OnlineChanged += OnConnectivityChanged;
        session.Changed += OnSessionChanged;

        _ = RunPeriodicSyncAsync(_periodicSyncCts.Token);
    }

    public SyncState State => _session.IsLocalMode ? SyncState.LocalOnly : _state;
    public int PendingCount => _session.IsLocalMode ? 0 : _pendingCount;
    public int ProblemCount => _session.IsLocalMode ? 0 : _problemCount;
    public string? LastError => _lastError;
    public DateTime? LastSyncedAtUtc => _lastSyncedAtUtc;

    public async Task WaitUntilIdleAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        _syncLock.Release();
    }

    public void NotifyLocalWrite()
    {
        if (_session.IsLocalMode || _session.CurrentCompany is null)
        {
            return;
        }

        _ = RefreshPendingCountAsync();

        if (!_connectivity.IsOnline)
        {
            UpdateState(SyncState.Offline);
            return;
        }

        // Debounce 2 seconds so rapid consecutive entries batch into one push.
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                await SyncInternalAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) =>
        SyncInternalAsync(cancellationToken);

    public async Task<IReadOnlyList<SyncIssueDto>> GetIssuesAsync(CancellationToken cancellationToken = default)
    {
        if (_session.IsLocalMode || _session.CurrentCompany is not { } company)
        {
            return [];
        }

        var issues = await _localStore.GetSyncIssuesAsync(company.CompanyId, cancellationToken);
        return issues.Select(i => new SyncIssueDto(i.Id, i.EntityType, i.EntityId, i.Operation, i.Attempts, i.Error, i.CreatedAtUtc)).ToList();
    }

    public async Task DiscardIssueAsync(long issueId, CancellationToken cancellationToken = default)
    {
        await _localStore.DiscardOutboxItemAsync(issueId, cancellationToken);
        await RefreshPendingCountAsync(cancellationToken);
        if (_connectivity.IsOnline)
        {
            await SyncInternalAsync(cancellationToken);
        }
    }

    public async Task RetryIssueAsync(long issueId, CancellationToken cancellationToken = default)
    {
        await _localStore.RetryOutboxItemAsync(issueId, cancellationToken);
        await RefreshPendingCountAsync(cancellationToken);
        if (_connectivity.IsOnline)
        {
            await SyncInternalAsync(cancellationToken);
        }
    }

    private async Task SyncInternalAsync(CancellationToken ct)
    {
        if (_session.IsLocalMode)
        {
            UpdateState(SyncState.LocalOnly);
            return;
        }

        if (!_session.IsSignedIn || _session.CurrentCompany is null)
        {
            return;
        }

        if (!_connectivity.IsOnline)
        {
            await RefreshPendingCountAsync(ct);
            UpdateState(SyncState.Offline);
            return;
        }

        if (!await _syncLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        try
        {
            UpdateState(SyncState.Syncing);
            var companyId = _session.CurrentCompany.CompanyId;
            _logger.LogInformation("Starting sync for company {CompanyId}", companyId);

            _sent.Clear();
            await EnsureCompanyDetailsCachedAsync(companyId, ct);
            await PushAsync(companyId, ct);
            await UploadPendingAttachmentsAsync(companyId, ct);
            var pulled = await PullAsync(companyId, ct);
            await RefreshPendingCountAsync(ct);

            _lastError = null;
            _lastSyncedAtUtc = DateTime.UtcNow;

            if (_problemCount > 0)
            {
                UpdateState(SyncState.Error);
            }
            else if (_pendingCount > 0)
            {
                UpdateState(SyncState.Pending);
            }
            else
            {
                UpdateState(_connectivity.IsOnline ? SyncState.Synced : SyncState.Offline);
            }

            if (pulled > 0)
            {
                DataChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            _lastError = ex.Message;
            UpdateState(_connectivity.IsOnline ? SyncState.Error : SyncState.Offline);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task EnsureCompanyDetailsCachedAsync(Guid companyId, CancellationToken ct)
    {
        var details = await _companyApi.GetCompanyAsync(companyId);
        if (details.Ok && details.Value is not null)
        {
            await _localStore.EnsureCompanyMetadataAsync(details.Value, ct);
        }
        else if (details.StatusCode == 403)
        {
            await WipeCompanyDataAsync(companyId);
            throw new InvalidOperationException("Access to this company was revoked.");
        }
    }

    /// <summary>
    /// Sends the queue, one change per record at a time because the next is based on the version this one
    /// produces. Items are claimed before sending and their results recorded afterwards, both under the store's
    /// queue gate, so a change made meanwhile is queued behind rather than folded into what is already on its way.
    /// </summary>
    private async Task PushAsync(Guid companyId, CancellationToken ct)
    {
        var tried = new HashSet<long>();
        while (true)
        {
            List<OutboxItem> batch;
            using (await _localStore.LockQueueAsync(ct))
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var queued = await db.Outbox.Where(o => o.CompanyId == companyId).OrderBy(o => o.Id).ToListAsync(ct);

                // The oldest change of each record. Conflicts wait for the user; so does everything behind them.
                batch = queued.GroupBy(o => o.EntityId).Select(g => g.First())
                    .Where(o => !tried.Contains(o.Id) && o.LastError?.StartsWith("Conflict", StringComparison.Ordinal) != true)
                    .OrderBy(o => o.Id)
                    .Take(200)
                    .ToList();
                if (batch.Count == 0)
                {
                    return;
                }

                foreach (var item in batch)
                {
                    item.Attempts++;
                    item.LastError = null;
                }

                await db.SaveChangesAsync(ct);
            }

            tried.UnionWith(batch.Select(o => o.Id));
            _logger.LogInformation("Pushing {Count} outbox items", batch.Count);
            var request = new PushRequest(batch
                .Select(o => new PushItem(o.EntityType, o.Operation, o.PayloadJson, o.Id.ToString(), o.BaseVersion))
                .ToList());
            var result = await _api.SendAsync<PushResponse>(HttpMethod.Post, $"/api/companies/{companyId}/sync/push", request);

            if (!result.Ok)
            {
                if (result.StatusCode == 403)
                {
                    _logger.LogWarning("Received 403 on push - revoked, wiping company data");
                    await WipeCompanyDataAsync(companyId);
                }
                else if (result.IsNetworkError)
                {
                    UpdateState(SyncState.Offline);
                }

                throw new InvalidOperationException($"Push failed: {result.Error} (status {result.StatusCode})");
            }

            if (result.Value is null)
            {
                throw new InvalidOperationException("Push returned null response");
            }

            var accepted = 0;
            using (await _localStore.LockQueueAsync(ct))
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var queued = await db.Outbox.Where(o => o.CompanyId == companyId).OrderBy(o => o.Id).ToListAsync(ct);
                foreach (var itemResult in result.Value.Results)
                {
                    if (queued.FirstOrDefault(o => o.Id.ToString() == itemResult.IdempotencyKey) is not { } item)
                    {
                        continue;
                    }

                    if (itemResult.Ok)
                    {
                        accepted++;
                        _sent.Add(item.EntityId);
                        db.Outbox.Remove(item);
                        if (itemResult.Version is { } version
                            && queued.FirstOrDefault(o => o.EntityId == item.EntityId && o.Id > item.Id) is { } next)
                        {
                            next.BaseVersion = version;
                        }

                        await UpdateLocalVersionAfterPushAsync(db, item.EntityType, item.EntityId, itemResult.FinalVoucherNo, itemResult.Version, ct);
                    }
                    else if (itemResult.Error?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        item.LastError = itemResult.Error.StartsWith("Conflict", StringComparison.OrdinalIgnoreCase)
                            ? itemResult.Error
                            : $"Conflict: {itemResult.Error}";
                    }
                    else
                    {
                        item.LastError = itemResult.Error ?? "Rejected by server.";
                    }
                }

                await db.SaveChangesAsync(ct);
            }

            if (accepted == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends photos taken or picked on this device, once their entry is on the server. Safe to repeat: a file
    /// the server already has (an earlier upload whose answer or local record was lost) is adopted, not sent again.
    /// </summary>
    private async Task UploadPendingAttachmentsAsync(Guid companyId, CancellationToken ct)
    {
        List<(Guid Id, Guid Entry, string Name, string Type, long Size, string Path)> pending;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            pending = (await (
                    from f in db.LocalAttachmentFiles
                    join a in db.Attachments on f.AttachmentId equals a.Id
                    join t in db.Transactions on a.TransactionId equals (Guid?)t.Id
                    where a.CompanyId == companyId && !a.IsDeleted && f.UploadStatus == UploadStatus.Pending && t.Version > 0
                    select new { a.Id, Entry = t.Id, a.FileName, a.ContentType, a.SizeBytes, f.LocalPath }).ToListAsync(ct))
                .Select(x => (x.Id, x.Entry, x.FileName, x.ContentType, x.SizeBytes, x.LocalPath))
                .ToList();
        }

        foreach (var item in pending)
        {
            if (!File.Exists(item.Path))
            {
                continue;
            }

            var onServer = await _remoteAttachments.GetAttachmentsAsync(companyId, item.Entry, ct);
            if (!onServer.Ok || onServer.Value is null)
            {
                continue; // try again next time
            }

            HashSet<Guid> known;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                known = (await db.Attachments.Where(a => a.TransactionId == item.Entry && a.Id != item.Id)
                    .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            }

            var uploaded = onServer.Value.FirstOrDefault(
                a => !known.Contains(a.Id) && a.FileName == item.Name && a.SizeBytes == item.Size);
            if (uploaded is null)
            {
                await using var stream = File.OpenRead(item.Path);
                var sent = await _remoteAttachments.UploadAttachmentAsync(companyId, stream, item.Name, item.Type, item.Entry, ct);
                if (!sent.Ok || sent.Value is null)
                {
                    continue;
                }

                uploaded = sent.Value;
            }

            await AdoptServerAttachmentAsync(companyId, item.Id, uploaded, item.Path, ct);
        }
    }

    /// <summary>Replaces the device's temporary record with the server's at once, so the photo never disappears.</summary>
    private async Task AdoptServerAttachmentAsync(
        Guid companyId, Guid localId, AttachmentDto server, string localPath, CancellationToken ct)
    {
        using var held = await _localStore.LockQueueAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.Attachments.FindAsync([localId], ct) is not { } local)
        {
            return;
        }

        if (await db.LocalAttachmentFiles.FindAsync([localId], ct) is { } file)
        {
            db.LocalAttachmentFiles.Remove(file);
        }

        db.Attachments.Remove(local);
        if (await db.Attachments.FindAsync([server.Id], ct) is null)
        {
            db.Attachments.Add(new Attachment
            {
                Id = server.Id,
                CompanyId = companyId,
                TransactionId = server.TransactionId,
                FileName = server.FileName,
                ContentType = server.ContentType,
                SizeBytes = server.SizeBytes,
                StorageKey = server.StorageKey,
                CreatedAtUtc = server.CreatedAtUtc,
                UpdatedAtUtc = server.CreatedAtUtc,
            });
        }

        if (await db.LocalAttachmentFiles.FindAsync([server.Id], ct) is null)
        {
            db.LocalAttachmentFiles.Add(new LocalAttachmentFile
            {
                AttachmentId = server.Id,
                LocalPath = localPath,
                UploadStatus = UploadStatus.Uploaded,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<int> PullAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var checkpoint = await db.SyncCheckpoints.SingleOrDefaultAsync(s => s.CompanyId == companyId, ct);

        long since = checkpoint?.LastPulledVersion ?? 0;
        const int take = 500;
        var pulledRows = new List<PulledRow>();
        long latestVersion = since;

        while (true)
        {
            var response = await _api.GetAsync<PullResponse>(
                $"/api/companies/{companyId}/sync/pull?since={since}&take={take}");

            if (!response.Ok || response.Value is null)
            {
                if (response.StatusCode == 403)
                {
                    await WipeCompanyDataAsync(companyId);
                }
                throw new InvalidOperationException($"Pull failed: {response.Error} (status {response.StatusCode})");
            }

            pulledRows.AddRange(response.Value.Rows);
            latestVersion = response.Value.LatestVersion;

            if (response.Value.Rows.Count < take) break;
            since = response.Value.LatestVersion;
        }

        if (pulledRows.Count == 0)
        {
            return 0;
        }

        // Held while applying, so nothing is edited between looking at the queue and writing the rows.
        using var held = await _localStore.LockQueueAsync(ct);

        // A queued change (waiting, refused or in conflict) is what the user last entered: leave it showing until
        // it uploads or they discard it, which downloads everything again.
        var queued = (await db.Outbox.Where(o => o.CompanyId == companyId).Select(o => o.EntityId).ToListAsync(ct)).ToHashSet();
        var incoming = pulledRows.Where(r => !queued.Contains(r.Id)).ToList();

        if (!await db.Companies.AnyAsync(c => c.Id == companyId, ct))
        {
            db.Companies.Add(new Company
            {
                Id = companyId,
                Name = _session.CurrentCompany!.Name,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        // Apply in dependency order: PartyCategory -> Account -> Transaction -> Attachment.
        var ordered = incoming.OrderBy(r => r.EntityType switch
        {
            "PartyCategory" => 0,
            "Account" => 1,
            "Transaction" => 2,
            _ => 3,
        }).ThenBy(r => r.Version);

        foreach (var row in ordered)
        {
            await UpsertPulledRowAsync(db, row, ct);
        }

        if (checkpoint is null)
        {
            db.SyncCheckpoints.Add(new SyncCheckpoint { CompanyId = companyId, LastPulledVersion = latestVersion });
        }
        else
        {
            checkpoint.LastPulledVersion = latestVersion;
        }

        await db.SaveChangesAsync(ct);
        return incoming.Count(r => !_sent.Contains(r.Id));
    }

    private static async Task UpsertPulledRowAsync(SqliteDbContext db, PulledRow row, CancellationToken ct)
    {
        switch (row.EntityType)
        {
            case "Account":
                await UpsertEntityAsync<Account>(db, row, ct);
                break;
            case "PartyCategory":
                await UpsertEntityAsync<PartyCategory>(db, row, ct);
                break;
            case "Transaction":
                await UpsertEntityAsync<Transaction>(db, row, ct);
                break;
            case "Attachment":
                await UpsertEntityAsync<Attachment>(db, row, ct);
                break;
        }
    }

    private static async Task UpsertEntityAsync<T>(SqliteDbContext db, PulledRow row, CancellationToken ct)
        where T : SyncEntity
    {
        var entity = JsonSerializer.Deserialize<T>(row.Payload, LocalBooksStore.SyncJson);
        if (entity is null) return;

        entity.Version = row.Version;
        entity.UpdatedAtUtc = row.UpdatedAtUtc;
        entity.UpdatedBy = row.UpdatedBy;
        entity.UpdatedOnDevice = row.UpdatedOnDevice;
        entity.IsDeleted = row.IsDeleted;

        var existing = await db.FindAsync<T>([row.Id], ct);
        if (existing is null)
        {
            db.Add(entity);
            return;
        }

        db.Entry(existing).CurrentValues.SetValues(entity);
    }

    private static async Task UpdateLocalVersionAfterPushAsync(
        SqliteDbContext db, string entityType, Guid entityId, string? finalVoucherNo, long? version, CancellationToken ct)
    {
        if (version is not { } v) return;

        switch (entityType)
        {
            case "Transaction" or "Adjustment":
                if (await db.Transactions.FindAsync([entityId], ct) is { } txn)
                {
                    txn.Version = v;
                    if (!string.IsNullOrWhiteSpace(finalVoucherNo))
                    {
                        txn.VoucherNo = finalVoucherNo;
                        txn.IsProvisionalNo = false;
                    }
                }
                break;
            case "Party" or "MoneyAccount" or "Category":
                if (await db.Accounts.FindAsync([entityId], ct) is { } acc) acc.Version = v;
                break;
            case "PartyCategory":
                if (await db.PartyCategories.FindAsync([entityId], ct) is { } cat) cat.Version = v;
                break;
            case "Attachment":
                if (await db.Attachments.FindAsync([entityId], ct) is { } att) att.Version = v;
                break;
        }
    }

    private async Task RefreshPendingCountAsync(CancellationToken ct = default)
    {
        if (_session.IsLocalMode || _session.CurrentCompany is not { } company)
        {
            _pendingCount = 0;
            _problemCount = 0;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var outboxPending = await db.Outbox.CountAsync(o => o.CompanyId == company.CompanyId, ct);
            var attPending = await (
                from f in db.LocalAttachmentFiles
                join a in db.Attachments on f.AttachmentId equals a.Id
                where a.CompanyId == company.CompanyId && !a.IsDeleted && f.UploadStatus == UploadStatus.Pending
                select f.AttachmentId).CountAsync(ct);
            _pendingCount = outboxPending + attPending;
            _problemCount = await db.Outbox.CountAsync(o => o.CompanyId == company.CompanyId && o.LastError != null, ct);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException)
        {
            // No database open yet (signed out).
        }
    }

    private async Task WipeCompanyDataAsync(Guid companyId)
    {
        _logger.LogWarning("Wiping all local data for company {CompanyId} due to revocation", companyId);
        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);

        var attachmentIds = await db.Attachments.Where(a => a.CompanyId == companyId).Select(a => a.Id).ToListAsync();
        await db.LocalAttachmentFiles.Where(f => attachmentIds.Contains(f.AttachmentId)).ExecuteDeleteAsync();
        await db.Outbox.Where(o => o.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Attachments.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Transactions.Where(t => t.CompanyId == companyId).ExecuteDeleteAsync();
        await db.PartyCategories.Where(c => c.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync();
        await db.SyncCheckpoints.Where(s => s.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync();
    }

    private void UpdateState(SyncState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnConnectivityChanged(object? sender, bool online)
    {
        if (_session.IsLocalMode)
        {
            UpdateState(SyncState.LocalOnly);
            return;
        }

        if (online && _session.CurrentCompany is not null)
        {
            _ = SyncInternalAsync(CancellationToken.None);
        }
        else
        {
            UpdateState(online ? SyncState.Synced : SyncState.Offline);
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_session.IsLocalMode)
        {
            _lastSyncedCompanyId = null;
            UpdateState(SyncState.LocalOnly);
            return;
        }

        _ = RefreshPendingCountAsync();

        // Trigger initial pull when user signs in or switches to a company.
        if (_session.IsSignedIn && _session.CurrentCompany is { } company && _lastSyncedCompanyId != company.CompanyId)
        {
            _lastSyncedCompanyId = company.CompanyId;
            if (_connectivity.IsOnline)
            {
                _ = SyncInternalAsync(CancellationToken.None);
            }
        }
        else if (!_session.IsSignedIn)
        {
            _lastSyncedCompanyId = null;
        }
    }

    private async Task RunPeriodicSyncAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                if (!_session.IsLocalMode && _connectivity.IsOnline && _session.CurrentCompany is not null)
                {
                    await SyncInternalAsync(CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic sync error");
            }
        }
    }

    public void Dispose()
    {
        _connectivity.OnlineChanged -= OnConnectivityChanged;
        _session.Changed -= OnSessionChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _periodicSyncCts.Cancel();
        _periodicSyncCts.Dispose();
        _syncLock.Dispose();
    }
}
