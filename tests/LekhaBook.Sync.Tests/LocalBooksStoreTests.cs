using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LekhaBook.Sync.Tests;

/// <summary>
/// End-to-end tests for LocalBooksStore: Offline Local Mode, Online-Synced Outbox coalescing,
/// attachments on local storage, conflict management, portable JSON backup/restore, and cloud migration bundles.
/// </summary>
public sealed class LocalBooksStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserDatabaseFactory _dbFactory;
    private readonly LocalBooksStore _store;
    private readonly Guid _userId = UserDatabaseFactory.LocalOfflineUserId;
    private static readonly HashSet<PartyType> AllPartyTypes = [PartyType.Customer, PartyType.Supplier];
    private static readonly HashSet<TransactionType> AllTxnTypes = [.. Enum.GetValues<TransactionType>()];

    public LocalBooksStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lekhabook-local-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbFactory = new UserDatabaseFactory(_tempDir);
        _dbFactory.OpenForUser(_userId);
        _store = new LocalBooksStore(_dbFactory, Path.Combine(_tempDir, "attachments"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Offline_local_mode_handles_full_bookkeeping_lifecycle_and_balances()
    {
        var created = await _store.CreateLocalCompanyAsync(new CreateCompanyRequest("Everest Traders", "9800000000", "Kathmandu", "123456789", "BS"), _userId);
        Assert.True(created.Ok, created.Error);
        var companyId = created.Value!.CompanyId;

        var moneyAccounts = (await _store.GetMoneyAccountsAsync(companyId)).Value!;
        var cash = moneyAccounts.Single(a => a.Name == "Cash");
        var bank = (await _store.AddMoneyAccountAsync(companyId, new SaveMoneyAccountRequest("Nabil Bank", "Bank", "Everest", "100200300", 10000m), _userId, enqueueOutbox: false)).Value!;
        var cust = (await _store.AddPartyAsync(companyId, new SavePartyRequest("Ram Sharma", "Customer", "9811111111", OpeningBalance: 1000m, OpeningSide: "ToReceive"), _userId, enqueueOutbox: false)).Value!;
        var fuel = (await _store.GetCategoriesAsync(companyId)).Value!.Single(c => c.Name == "Fuel");

        var today = CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);
        var payIn = await _store.AddTransactionAsync(companyId, new CreateTransactionRequest("PaymentIn", today, 400m, cust.Id, cash.Id, "Part payment"), _userId, enqueueOutbox: false);
        Assert.True(payIn.Ok, payIn.Error);
        Assert.True((await _store.AddTransactionAsync(companyId, new CreateTransactionRequest("Expense", today, 150.25m, cash.Id, fuel.Id, "Petrol"), _userId, enqueueOutbox: false)).Ok);
        Assert.True((await _store.AddTransactionAsync(companyId, new CreateTransactionRequest("TransferMoney", today, 200m, cash.Id, bank.Id, "Deposit"), _userId, enqueueOutbox: false)).Ok);
        Assert.True((await _store.AdjustBalanceAsync(companyId, cust.Id, new AdjustBalanceRequest(Increase: true, 100m, Guid.CreateVersion7(), today, "Adjustment"), _userId, enqueueOutbox: false)).Ok);

        // Customer: 1000 - 400 + 100 = 700
        var custAfter = (await _store.GetPartyAsync(companyId, cust.Id, AllPartyTypes)).Value!;
        Assert.Equal(700m, custAfter.Balance);

        // Cash: 0 + 400 - 150.25 - 200 = 49.75
        var cashAfter = (await _store.GetMoneyAccountAsync(companyId, cash.Id)).Value!;
        Assert.Equal(49.75m, cashAfter.Balance);

        // Bank: 10000 + 200 = 10200
        var bankAfter = (await _store.GetMoneyAccountAsync(companyId, bank.Id)).Value!;
        Assert.Equal(10200m, bankAfter.Balance);

        // Ledger agrees with balances
        var ledger = (await _store.GetPartyLedgerAsync(companyId, cust.Id, AllPartyTypes, AllTxnTypes)).Value!;
        Assert.Equal(700m, ledger.ClosingBalance);
        Assert.Equal(2, ledger.Lines.Count);

        // Local mode writes no outbox items
        await using var db = _dbFactory.CreateDbContext();
        Assert.Empty(await db.Outbox.ToListAsync());
    }

    [Fact]
    public async Task Outbox_coalesces_unsynced_create_edit_and_delete_cleanly()
    {
        var companyId = (await _store.CreateLocalCompanyAsync(new CreateCompanyRequest("Sync Co"), _userId)).Value!.CompanyId;

        // 1. Create party offline -> 1 Create outbox item
        var party = (await _store.AddPartyAsync(companyId, new SavePartyRequest("Initial Name", "Customer"), _userId, enqueueOutbox: true)).Value!;
        await using (var db = _dbFactory.CreateDbContext())
        {
            var items = await db.Outbox.Where(o => o.EntityId == party.Id).ToListAsync();
            Assert.Single(items);
            Assert.Equal("Create", items[0].Operation);
        }

        // 2. Edit party before it ever synced -> coalesces into the SAME Create outbox item (no conflicting Update!)
        var updated = await _store.UpdatePartyAsync(companyId, party.Id, new SavePartyRequest("Renamed Offline", "Customer", "9841000000"), _userId, enqueueOutbox: true);
        Assert.True(updated.Ok, updated.Error);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var items = await db.Outbox.Where(o => o.EntityId == party.Id).ToListAsync();
            Assert.Single(items);
            Assert.Equal("Create", items[0].Operation);
            Assert.Contains("Renamed Offline", items[0].PayloadJson);
        }

        // 3. Delete party before it ever synced -> removes the Create outbox item completely
        Assert.True((await _store.DeletePartyAsync(companyId, party.Id, _userId, enqueueOutbox: true)).Ok);
        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Empty(await db.Outbox.Where(o => o.EntityId == party.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Offline_attachments_save_read_and_build_migration_bundles()
    {
        var companyId = (await _store.CreateLocalCompanyAsync(new CreateCompanyRequest("Shop A"), _userId)).Value!.CompanyId;
        var cash = (await _store.GetMoneyAccountsAsync(companyId)).Value!.Single(a => a.Name == "Cash");
        var party = (await _store.AddPartyAsync(companyId, new SavePartyRequest("Sita", "Customer"), _userId, enqueueOutbox: false)).Value!;
        var today = CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);
        var txn = (await _store.AddTransactionAsync(companyId, new CreateTransactionRequest("PaymentIn", today, 250m, party.Id, cash.Id), _userId, enqueueOutbox: false)).Value!;

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var saved = await _store.SaveLocalAttachmentAsync(companyId, txn.Id, "bill.png", "image/png", png, _userId, pendingUpload: false);
        Assert.True(saved.Ok, saved.Error);

        var read = await _store.TryReadLocalAttachmentFileAsync(companyId, saved.Value!.Id);
        Assert.NotNull(read);
        Assert.Equal(png, read!.Content);

        var bundles = await _store.BuildMigrationBundlesAsync();
        var bundle = Assert.Single(bundles);
        Assert.Equal(companyId, bundle.Snapshot.CompanyId);
        Assert.Single(bundle.Snapshot.Transactions);
        Assert.Single(bundle.Attachments);
        Assert.Equal(txn.Id, bundle.Attachments[0].TransactionId);
    }

    [Fact]
    public async Task Portable_json_backup_and_restore_preserves_balances_with_new_ids()
    {
        var companyId = (await _store.CreateLocalCompanyAsync(new CreateCompanyRequest("Backup Biz"), _userId)).Value!.CompanyId;
        var cash = (await _store.GetMoneyAccountsAsync(companyId)).Value!.Single(a => a.Name == "Cash");
        var party = (await _store.AddPartyAsync(companyId, new SavePartyRequest("Gita", "Customer", OpeningBalance: 500m, OpeningSide: "ToReceive"), _userId, enqueueOutbox: false)).Value!;
        var today = CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);
        Assert.True((await _store.AddTransactionAsync(companyId, new CreateTransactionRequest("PaymentIn", today, 200m, party.Id, cash.Id), _userId, enqueueOutbox: false)).Ok);

        var exported = await _store.ExportCompanyBackupJsonAsync(companyId);
        Assert.True(exported.Ok, exported.Error);

        var restored = await _store.ImportCompanyBackupJsonAsync(exported.Value!, _userId);
        Assert.True(restored.Ok, restored.Error);
        Assert.NotEqual(companyId, restored.Value!.CompanyId);

        var restoredParties = (await _store.GetPartiesAsync(restored.Value.CompanyId, AllPartyTypes)).Value!;
        var restoredParty = Assert.Single(restoredParties);
        Assert.Equal("Gita", restoredParty.Name);
        Assert.Equal(300m, restoredParty.Balance);
    }
}
