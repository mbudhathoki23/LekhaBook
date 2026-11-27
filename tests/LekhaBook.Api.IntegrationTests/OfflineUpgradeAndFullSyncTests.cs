using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>
/// Verifies end-to-end Offline Local Mode -> Online Cloud Upgrade (POST /api/companies/migrate),
/// full Sync Push for Parties/MoneyAccounts/Categories/Adjustments, and /health/ready.
/// </summary>
public sealed class OfflineUpgradeAndFullSyncTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static DateOnly Today => CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [PostgresFact]
    public async Task Offline_local_sqlite_books_migrate_cleanly_and_idempotently_to_online_cloud()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lekhabook-upgrade-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var localUserId = UserDatabaseFactory.LocalOfflineUserId;
            var factory = new UserDatabaseFactory(tempDir);
            factory.OpenForUser(localUserId);
            var localStore = new LocalBooksStore(factory, Path.Combine(tempDir, "attachments"));

            // Build real offline books on SQLite without any online account.
            var created = await localStore.CreateLocalCompanyAsync(new CreateCompanyRequest("Himalaya Store", "9841223344", "Pokhara", "301234567", "BS"), localUserId);
            var companyId = created.Value!.CompanyId;

            var cat = (await localStore.AddPartyCategoryAsync(companyId, new SavePartyCategoryRequest("Wholesale"), localUserId, enqueueOutbox: false)).Value!;
            var cash = (await localStore.GetMoneyAccountsAsync(companyId)).Value!.Single(a => a.Name == "Cash");
            var bank = (await localStore.AddMoneyAccountAsync(companyId, new SaveMoneyAccountRequest("Global IME", "Bank", "Himalaya", "0123456789", 5000m), localUserId, enqueueOutbox: false)).Value!;
            var party = (await localStore.AddPartyAsync(companyId, new SavePartyRequest("Bikash", "Customer", "9851000000", CategoryId: cat.Id, OpeningBalance: 1500m, OpeningSide: "ToReceive"), localUserId, enqueueOutbox: false)).Value!;
            var customExp = (await localStore.AddCategoryAsync(companyId, new SaveCategoryRequest("Shop Rent", "Expense", Guid.CreateVersion7()), localUserId, enqueueOutbox: false)).Value!;

            Assert.True((await localStore.AddTransactionAsync(companyId, new CreateTransactionRequest("PaymentIn", Today, 500m, party.Id, cash.Id, "Cash received"), localUserId, enqueueOutbox: false)).Ok);
            Assert.True((await localStore.AddTransactionAsync(companyId, new CreateTransactionRequest("Expense", Today, 1200m, bank.Id, customExp.Id, "Monthly rent"), localUserId, enqueueOutbox: false)).Ok);
            Assert.True((await localStore.AdjustBalanceAsync(companyId, party.Id, new AdjustBalanceRequest(Increase: true, 200m, Guid.CreateVersion7(), Today, "Late fee"), localUserId, enqueueOutbox: false)).Ok);

            var bundle = Assert.Single(await localStore.BuildMigrationBundlesAsync());

            // Now user creates an online account and upgrades their offline data!
            var owner = await api.NewUserAsync();
            var migrateResponse = await owner.Http.PostAsJsonAsync("/api/companies/migrate", bundle.Snapshot);
            Assert.Equal(HttpStatusCode.Created, migrateResponse.StatusCode);
            var migrated = (await migrateResponse.Content.ReadFromJsonAsync<MigrateOfflineCompanyResponse>())!;
            Assert.False(migrated.AlreadyExisted);
            Assert.Equal(companyId, migrated.Company.CompanyId);
            Assert.Equal("Owner", migrated.Company.Role);
            Assert.Equal(3, migrated.TransactionsMigrated);

            // Verify server balances match offline SQLite balances to the exact paisa!
            var serverParty = await owner.Http.GetFromJsonAsync<PartyDto>($"/api/companies/{companyId}/parties/{party.Id}");
            var serverCash = await owner.Http.GetFromJsonAsync<MoneyAccountDto>($"/api/companies/{companyId}/accounts/{cash.Id}");
            var serverBank = await owner.Http.GetFromJsonAsync<MoneyAccountDto>($"/api/companies/{companyId}/accounts/{bank.Id}");
            Assert.Equal(1200m, serverParty!.Balance); // 1500 - 500 + 200
            Assert.Equal(500m, serverCash!.Balance);   // 0 + 500
            Assert.Equal(3800m, serverBank!.Balance);  // 5000 - 1200

            // Idempotent retry: re-sending after a simulated network drop returns 200 OK with AlreadyExisted=true and zero duplicates.
            var retryResponse = await owner.Http.PostAsJsonAsync("/api/companies/migrate", bundle.Snapshot);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            var retried = (await retryResponse.Content.ReadFromJsonAsync<MigrateOfflineCompanyResponse>())!;
            Assert.True(retried.AlreadyExisted);
            Assert.Equal(1200m, (await owner.Http.GetFromJsonAsync<PartyDto>($"/api/companies/{companyId}/parties/{party.Id}"))!.Balance);

            // Security: a different user cannot hijack an already migrated company ID.
            var stranger = await api.NewUserAsync();
            var hijack = await stranger.Http.PostAsJsonAsync("/api/companies/migrate", bundle.Snapshot);
            Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [PostgresFact]
    public async Task Full_sync_push_supports_parties_money_accounts_categories_adjustments_and_opening_conflicts()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";

        var partyId = Guid.CreateVersion7();
        var bankId = Guid.CreateVersion7();
        var catId = Guid.CreateVersion7();
        var adjId = Guid.CreateVersion7();

        PushItem Item(string type, string op, object payload, long baseVersion = 0) =>
            new(type, op, JsonSerializer.Serialize(payload, Web), Guid.NewGuid().ToString("N"), baseVersion);

        var batch1 = new PushRequest([
            Item("Party", "Create", new SavePartyRequest("Offline Customer", "Customer", "9801112222", OpeningBalance: 1000m, OpeningSide: "ToReceive", Id: partyId)),
            Item("MoneyAccount", "Create", new SaveMoneyAccountRequest("NICA Bank", "Bank", "Owner", "99887766", 2500m, Id: bankId)),
            Item("Category", "Create", new SaveCategoryRequest("Internet", "Expense", catId)),
            Item("Adjustment", "Create", new SyncAdjustmentPayload(partyId, new AdjustBalanceRequest(Increase: true, 250m, adjId, Today, "Offline adjust"))),
        ]);

        var resp1 = await owner.Http.PostAsJsonAsync($"{url}/sync/push", batch1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var res1 = (await resp1.Content.ReadFromJsonAsync<PushResponse>())!.Results;
        Assert.All(res1, r => Assert.True(r.Ok, r.Error));

        var partyVersion = res1[0].Version!.Value;
        Assert.Equal(1250m, (await owner.Http.GetFromJsonAsync<PartyDto>($"{url}/parties/{partyId}"))!.Balance);
        Assert.Equal(2500m, (await owner.Http.GetFromJsonAsync<MoneyAccountDto>($"{url}/accounts/{bankId}"))!.Balance);

        // Non-money edit at stale version -> LWW succeeds; opening-balance edit at stale version -> Conflict (D8)!
        var editOnline = await owner.Http.PutAsJsonAsync($"{url}/parties/{partyId}",
            new SavePartyRequest("Online Updated", "Customer", "9801112222", OpeningBalance: 1000m, OpeningSide: "ToReceive"));
        editOnline.EnsureSuccessStatusCode();

        var staleOpeningEdit = new PushRequest([
            Item("Party", "Update", new SyncPartyUpdatePayload(partyId,
                new SavePartyRequest("Offline Opening Edit", "Customer", "9801112222", OpeningBalance: 9999m, OpeningSide: "ToReceive")), baseVersion: partyVersion),
        ]);
        var resp2 = await owner.Http.PostAsJsonAsync($"{url}/sync/push", staleOpeningEdit);
        var res2 = Assert.Single((await resp2.Content.ReadFromJsonAsync<PushResponse>())!.Results);
        Assert.False(res2.Ok);
        Assert.StartsWith("Conflict", res2.Error);
    }

    [PostgresFact]
    public async Task Health_ready_endpoint_confirms_database_connectivity()
    {
        var response = await api.Factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
