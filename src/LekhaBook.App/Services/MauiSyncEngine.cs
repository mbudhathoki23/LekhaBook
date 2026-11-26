using System.Text.Json;
using LekhaBook.Client;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LekhaBook.App.Services;

/// <summary>
/// Real offline-first sync engine for MAUI (phase 8).
/// Manages outbox, push/pull, conflicts, provisional voucher numbers, and revocation wipe.
/// </summary>
public sealed class MauiSyncEngine : ISyncService, IDisposable
{
    private readonly IDbContextFactory<SqliteDbContext> _dbFactory;
    private readonly ISessionService _session;
    private readonly IConnectivityService _connectivity;
    private readonly LekhaBookApi _api;
    private readonly ILogger<MauiSyncEngine> _logger;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private CancellationTokenSource? _periodicSyncCts;

    private SyncState _state = SyncState.Synced;
    private int _pendingCount = 0;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public event EventHandler? StateChanged;

    public MauiSyncEngine(
        IDbContextFactory<SqliteDbContext> dbFactory,
        ISessionService session,
        IConnectivityService connectivity,
        LekhaBookApi api,
        ILogger<MauiSyncEngine> logger)
    {
        _dbFactory = dbFactory;
        _session = session;
        _connectivity = connectivity;
        _api = api;
        _logger = logger;

        connectivity.OnlineChanged += OnConnectivityChanged;
        session.Changed += OnSessionChanged;

        _periodicSyncCts = new CancellationTokenSource();
        _ = RunPeriodicSyncAsync(_periodicSyncCts.Token);
    }

    public SyncState State => _state;
    public int PendingCount => _pendingCount;

    public Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        return SyncInternalAsync(cancellationToken);
    }

    private async Task SyncInternalAsync(CancellationToken ct)
    {
        if (!_connectivity.IsOnline || _session.CurrentCompany is null)
        {
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
            _logger.LogInformation("Starting sync for company {CompanyId}", _session.CurrentCompany.CompanyId);

            await PushAsync(ct);
            await PullAsync(ct);
            await RefreshPendingCountAsync();

            UpdateState(_connectivity.IsOnline ? SyncState.Synced : SyncState.Offline);
            _logger.LogInformation("Sync completed, pending={PendingCount}", _pendingCount);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            UpdateState(SyncState.Error);
        }
        finally
        {
            _syncLock.Release();
        }
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
        _ = RefreshPendingCountAsync();
    }

    private async Task PushAsync(CancellationToken ct)
    {
        var companyId = _session.CurrentCompany!.CompanyId;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var outboxItems = await db.Outbox
            .Where(o => o.CompanyId == companyId)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        if (outboxItems.Count == 0)
        {
            _logger.LogDebug("No outbox items to push");
            return;
        }

        _logger.LogInformation("Pushing {Count} outbox items", outboxItems.Count);

        var pushItems = outboxItems.Select(o => new PushItem(
            o.EntityType,
            o.Operation,
            o.PayloadJson,
            o.Id.ToString())).ToList();

        var request = new PushRequest(pushItems);
        var result = await _api.SendAsync<PushResponse>(HttpMethod.Post,
            $"/api/companies/{companyId}/sync/push", request);

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

        foreach (var itemResult in result.Value.Results)
        {
            var outboxItem = outboxItems.FirstOrDefault(o => o.Id.ToString() == itemResult.IdempotencyKey);
            if (outboxItem is null)
                continue;

            if (itemResult.Ok)
            {
                db.Outbox.Remove(outboxItem);

                if (!string.IsNullOrWhiteSpace(itemResult.FinalVoucherNo))
                {
                    await UpdateLocalVoucherNoAsync(db, outboxItem.EntityType, outboxItem.EntityId,
                        itemResult.FinalVoucherNo, itemResult.Version, ct);
                }
            }
            else if (itemResult.Error?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) == true)
            {
                outboxItem.LastError = $"Conflict: {itemResult.Error}";
                outboxItem.Attempts++;
                _logger.LogWarning("Conflict on push for {EntityType} {EntityId}: {Error}",
                    outboxItem.EntityType, outboxItem.EntityId, itemResult.Error);
            }
            else
            {
                outboxItem.Attempts++;
                outboxItem.LastError = itemResult.Error;
                _logger.LogWarning("Push error for {EntityType} {EntityId}: {Error} (attempt {Attempts})",
                    outboxItem.EntityType, outboxItem.EntityId, itemResult.Error, outboxItem.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PullAsync(CancellationToken ct)
    {
        var companyId = _session.CurrentCompany!.CompanyId;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var checkpoint = await db.SyncCheckpoints
            .SingleOrDefaultAsync(s => s.CompanyId == companyId, ct);

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

            if (response.Value.Rows.Count < take)
                break;

            since = response.Value.LatestVersion;
        }

        if (pulledRows.Count == 0)
        {
            _logger.LogDebug("No new rows to pull");
            return;
        }

        _logger.LogInformation("Pulled {Count} rows, latest version {Version}", pulledRows.Count, latestVersion);

        foreach (var row in pulledRows)
        {
            await UpsertPulledRowAsync(db, row, ct);
        }

        if (checkpoint is null)
        {
            db.SyncCheckpoints.Add(new SyncCheckpoint
            {
                CompanyId = companyId,
                LastPulledVersion = latestVersion
            });
        }
        else
        {
            checkpoint.LastPulledVersion = latestVersion;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledRowAsync(SqliteDbContext db, PulledRow row, CancellationToken ct)
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

    private async Task UpsertEntityAsync<T>(SqliteDbContext db, PulledRow row, CancellationToken ct)
        where T : class
    {
        var entity = JsonSerializer.Deserialize<T>(row.Payload, _json);
        if (entity is null)
            return;

        var existing = await db.FindAsync<T>(row.Id, ct);
        if (existing is null)
        {
            typeof(T).GetProperty("Version")?.SetValue(entity, row.Version);
            typeof(T).GetProperty("UpdatedAtUtc")?.SetValue(entity, row.UpdatedAtUtc);
            typeof(T).GetProperty("UpdatedBy")?.SetValue(entity, row.UpdatedBy);
            typeof(T).GetProperty("UpdatedOnDevice")?.SetValue(entity, row.UpdatedOnDevice);
            typeof(T).GetProperty("IsDeleted")?.SetValue(entity, row.IsDeleted);
            db.Add(entity);
            return;
        }

        var existingVersion = (long?)typeof(T).GetProperty("Version")?.GetValue(existing) ?? 0;
        if (existingVersion > row.Version)
        {
            _logger.LogWarning("Pull conflict for {EntityType} {Id}: local version {Local} > server {Server}",
                row.EntityType, row.Id, existingVersion, row.Version);
        }

        var props = typeof(T).GetProperties();
        foreach (var prop in props)
        {
            if (prop.Name is "Id" or "CompanyId" or "CreatedAtUtc" or "CreatedBy" or "Version" or "UpdatedAtUtc" or "UpdatedBy" or "UpdatedOnDevice" or "IsDeleted")
            {
                continue;
            }
            if (prop.CanWrite)
            {
                prop.SetValue(existing, prop.GetValue(entity));
            }
        }

        typeof(T).GetProperty("Version")?.SetValue(existing, row.Version);
        typeof(T).GetProperty("UpdatedAtUtc")?.SetValue(existing, row.UpdatedAtUtc);
        typeof(T).GetProperty("UpdatedBy")?.SetValue(existing, row.UpdatedBy);
        typeof(T).GetProperty("UpdatedOnDevice")?.SetValue(existing, row.UpdatedOnDevice);
        typeof(T).GetProperty("IsDeleted")?.SetValue(existing, row.IsDeleted);
    }

    private async Task UpdateLocalVoucherNoAsync(SqliteDbContext db, string entityType, Guid entityId,
        string finalVoucherNo, long? version, CancellationToken ct)
    {
        if (entityType != "Transaction")
            return;

        var txn = await db.Transactions.FindAsync([entityId], ct);
        if (txn is not null)
        {
            txn.VoucherNo = finalVoucherNo;
            txn.IsProvisionalNo = false;
            if (version.HasValue)
            {
                txn.Version = version.Value;
            }
        }
    }

    private async Task RefreshPendingCountAsync(CancellationToken ct = default)
    {
        var companyId = _session.CurrentCompany?.CompanyId;
        if (companyId is not { } cid)
        {
            _pendingCount = 0;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _pendingCount = await db.Outbox.CountAsync(o => o.CompanyId == cid, ct);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WipeCompanyDataAsync(Guid companyId)
    {
        _logger.LogWarning("Wiping all local data for company {CompanyId} due to revocation", companyId);

        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);

        var attachmentIds = await db.Attachments
            .Where(a => a.CompanyId == companyId)
            .Select(a => a.Id)
            .ToListAsync();

        await db.LocalAttachmentFiles
            .Where(f => attachmentIds.Contains(f.AttachmentId))
            .ExecuteDeleteAsync();

        await db.Outbox.Where(o => o.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Attachments.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Transactions.Where(t => t.CompanyId == companyId).ExecuteDeleteAsync();
        await db.PartyCategories.Where(c => c.CompanyId == companyId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync();
        await db.SyncCheckpoints.Where(s => s.CompanyId == companyId).ExecuteDeleteAsync();
    }

    private async Task RunPeriodicSyncAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                if (_connectivity.IsOnline && _session.CurrentCompany is not null)
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
        _periodicSyncCts?.Cancel();
        _periodicSyncCts?.Dispose();
        _syncLock.Dispose();
    }
}
