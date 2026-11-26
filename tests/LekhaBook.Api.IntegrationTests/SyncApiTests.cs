using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using LekhaBook.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Device sync push and pull (D17): no more can be done through it than through an online save.</summary>
public sealed class SyncApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static DateOnly Today => CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static PushItem Item(string type, string operation, object payload, long baseVersion = 0) =>
        new(type, operation, JsonSerializer.Serialize(payload, Web), Guid.NewGuid().ToString("N"), baseVersion);

    private static PushItem NewEntry(Guid from, Guid to, decimal amount, Guid? id = null, DateOnly? date = null, string type = "TransferMoney") =>
        Item("Transaction", "Create", new CreateTransactionRequest(type, date ?? Today, amount, from, to, Id: id ?? Guid.CreateVersion7()));

    private static async Task<IReadOnlyList<PushItemResult>> PushAsync(ApiFixture.UserClient user, string url, params PushItem[] items)
    {
        var response = await user.Http.PostAsJsonAsync($"{url}/sync/push", new PushRequest(items));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results;
    }

    [PostgresFact]
    public async Task Changes_that_break_the_rules_are_refused_one_by_one_without_a_403()
    {
        var mine = await SetUpAsync();
        var theirs = await SetUpAsync();

        var results = await PushAsync(mine.Owner, mine.Url,
            NewEntry(theirs.Cash, mine.Cash, 999m),                       // another company's account
            NewEntry(mine.Cash, mine.Bank, -5m),
            NewEntry(mine.Cash, mine.Bank, 1.005m),
            NewEntry(mine.Cash, mine.Cash, 5m),
            NewEntry(mine.Cash, mine.Bank, 5m, date: Today.AddDays(1)),
            Item("Transaction", "Create", new CreateTransactionRequest("TransferMoney", Today, 5m, mine.Cash, mine.Bank)), // no id
            Item("Attachment", "Create", new { Id = Guid.NewGuid(), FileName = "x", ContentType = "text/plain", StorageKey = "C:/secret" }),
            new PushItem("Transaction", "Create", "not json", "bad"),
            new PushItem("Account", "Create", "{}", "unknown"),
            NewEntry(mine.Cash, mine.Bank, 7m));                          // a good one after the bad ones

        Assert.Equal(10, results.Count);
        Assert.All(results.Take(9), r => Assert.False(r.Ok, r.IdempotencyKey));
        Assert.All(results.Take(9), r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));
        Assert.True(results[9].Ok, results[9].Error);
        Assert.Equal(-7m, await mine.BalanceAsync(mine.Cash));
        Assert.Equal(0m, await theirs.BalanceAsync(theirs.Cash));

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        Assert.Empty(await db.Attachments.Where(a => a.CompanyId == mine.CompanyId).ToListAsync());
        Assert.Single(await db.Transactions.Where(t => t.CompanyId == mine.CompanyId).ToListAsync());
    }

    [PostgresFact]
    public async Task Entry_person_rules_apply_to_push_and_only_non_members_get_403()
    {
        var books = await SetUpAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, clerk, "EntryPerson");
        var ownersId = Guid.CreateVersion7();
        await PushAsync(books.Owner, books.Url, NewEntry(books.Cash, books.Bank, 10m, ownersId));

        var results = await PushAsync(clerk, books.Url,
            NewEntry(books.Cash, books.Bank, 1m, date: Today.AddDays(-1)),
            Item("Transaction", "Void", new SyncVoidPayload(ownersId), baseVersion: 1),
            Item("PartyCategory", "Create", new SavePartyCategoryRequest("Retail", Guid.CreateVersion7())),
            NewEntry(books.Cash, books.Bank, 2m));

        Assert.Equal([false, false, false, true], results.Select(r => r.Ok));
        Assert.Equal(-12m, await books.BalanceAsync(books.Cash));

        var outsider = await api.NewUserAsync();
        var refused = await outsider.Http.PostAsJsonAsync($"{books.Url}/sync/push", new PushRequest([]));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.GetAsync($"{books.Url}/sync/pull?since=0&take=10")).StatusCode);

        var tooMany = Enumerable.Range(0, 201).Select(_ => new PushItem("Account", "Create", "{}", "k")).ToArray();
        var big = await books.Owner.Http.PostAsJsonAsync($"{books.Url}/sync/push", new PushRequest(tooMany));
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
    }

    [PostgresFact]
    public async Task Retries_add_nothing_and_stale_changes_are_conflicts()
    {
        var books = await SetUpAsync();
        var id = Guid.CreateVersion7();
        var create = NewEntry(books.Cash, books.Bank, 100m, id);

        var first = Assert.Single(await PushAsync(books.Owner, books.Url, create));
        var again = Assert.Single(await PushAsync(books.Owner, books.Url, create));
        Assert.True(first.Ok, first.Error);
        Assert.True(again.Ok, again.Error);
        Assert.Equal(first.Version, again.Version);
        Assert.Equal(-100m, await books.BalanceAsync(books.Cash));

        PushItem Change(decimal amount, long baseVersion) => Item("Transaction", "Update",
            new SyncUpdatePayload(id, new UpdateTransactionRequest(Today, amount, books.Cash, books.Bank)), baseVersion);

        var edited = Assert.Single(await PushAsync(books.Owner, books.Url, Change(150m, first.Version!.Value)));
        var replayed = Assert.Single(await PushAsync(books.Owner, books.Url, Change(150m, first.Version!.Value)));
        var stale = Assert.Single(await PushAsync(books.Owner, books.Url, Change(175m, first.Version!.Value)));
        Assert.True(edited.Ok, edited.Error);
        Assert.True(replayed.Ok, replayed.Error);
        Assert.False(stale.Ok);
        Assert.StartsWith("Conflict", stale.Error);
        Assert.Equal(-150m, await books.BalanceAsync(books.Cash));

        var staleVoid = Assert.Single(await PushAsync(books.Owner, books.Url,
            Item("Transaction", "Void", new SyncVoidPayload(id), first.Version!.Value)));
        Assert.StartsWith("Conflict", staleVoid.Error);
        var voidIt = Item("Transaction", "Void", new SyncVoidPayload(id), edited.Version!.Value);
        Assert.True(Assert.Single(await PushAsync(books.Owner, books.Url, voidIt)).Ok);
        Assert.True(Assert.Single(await PushAsync(books.Owner, books.Url, voidIt)).Ok);
        Assert.Equal(0m, await books.BalanceAsync(books.Cash));

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var audit = await db.AuditLogs.Where(a => a.EntityId == id).OrderBy(a => a.AtUtc).ToListAsync();
        Assert.Equal(["Create", "Update", "Void"], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.NotNull(a.UserId));
    }

    [PostgresFact]
    public async Task Pull_pages_through_every_change_exactly_once()
    {
        var books = await SetUpAsync();
        for (var i = 1; i <= 5; i++)
        {
            await PushAsync(books.Owner, books.Url,
                NewEntry(books.Cash, books.Bank, i),
                Item("PartyCategory", "Create", new SavePartyCategoryRequest($"Group {i}", Guid.CreateVersion7())));
        }

        var seen = new List<PulledRow>();
        long since = 0;
        const int take = 2;
        while (true) // the way the device loops
        {
            var page = (await books.Owner.Http.GetFromJsonAsync<PullResponse>($"{books.Url}/sync/pull?since={since}&take={take}"))!;
            seen.AddRange(page.Rows);
            since = page.LatestVersion;
            if (page.Rows.Count < take)
            {
                break;
            }
        }

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var company = books.CompanyId;
        var expected = (await db.Accounts.Where(x => x.CompanyId == company).Select(x => x.Id).ToListAsync())
            .Concat(await db.PartyCategories.Where(x => x.CompanyId == company).Select(x => x.Id).ToListAsync())
            .Concat(await db.Transactions.Where(x => x.CompanyId == company).Select(x => x.Id).ToListAsync())
            .ToList();

        Assert.Equal(expected.Count, seen.Count);
        Assert.Equal(expected.Order(), seen.Select(r => r.Id).Order());
        Assert.Equal(seen.Select(r => r.Version).Order(), seen.Select(r => r.Version));
        var last = await db.CompanySyncStates.Where(s => s.CompanyId == company).Select(s => s.LastVersion).SingleAsync();
        Assert.Equal(last, since);

        var nothingNew = await books.Owner.Http.GetFromJsonAsync<PullResponse>($"{books.Url}/sync/pull?since={since}");
        Assert.Empty(nothingNew!.Rows);
    }

    [PostgresFact]
    public async Task A_stored_file_path_can_never_reach_outside_the_company_folder()
    {
        var books = await SetUpAsync();
        var entryId = Guid.CreateVersion7();
        await PushAsync(books.Owner, books.Url, NewEntry(books.Cash, books.Bank, 5m, entryId));
        var marker = Path.Combine(Path.GetTempPath(), $"lekhabook-marker-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(marker, "not an attachment");
        try
        {
            string[] keys = [marker, Path.Combine("..", "..", "appsettings.json"), "..", ""];
            var ids = new List<Guid>();
            using (var scope = api.Factory.Services.CreateScope())
            {
                // Put the rows straight in the database, as if some earlier or future write had let a path through.
                var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
                foreach (var key in keys)
                {
                    var row = new Attachment
                    {
                        CompanyId = books.CompanyId,
                        TransactionId = entryId,
                        FileName = "x.txt",
                        ContentType = "text/plain",
                        StorageKey = key,
                    };
                    db.Attachments.Add(row);
                    ids.Add(row.Id);
                }

                await db.SaveChangesAsync();
            }

            foreach (var id in ids)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await books.Owner.Http.GetAsync($"{books.Url}/attachments/{id}")).StatusCode);
            }
        }
        finally
        {
            File.Delete(marker);
        }
    }

    // ---------------- Helpers ----------------

    private async Task<Books> SetUpAsync()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";
        var accounts = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!;
        return new Books(owner, companyId, url, accounts.Single(a => a.Name == "Cash").Id, accounts.Single(a => a.Name == "Online Wallet").Id);
    }

    private sealed record Books(ApiFixture.UserClient Owner, Guid CompanyId, string Url, Guid Cash, Guid Bank)
    {
        public async Task<decimal> BalanceAsync(Guid account) =>
            (await Owner.Http.GetFromJsonAsync<MoneyAccountDto>($"{Url}/accounts/{account}"))!.Balance;
    }
}
