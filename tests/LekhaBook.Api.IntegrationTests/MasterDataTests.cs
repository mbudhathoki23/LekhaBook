using System.Net;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using LekhaBook.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Parties and money accounts through the API (phase 5, D14).</summary>
public sealed class MasterDataTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // ---------------- Parties ----------------

    [PostgresFact]
    public async Task Party_create_is_idempotent_and_its_balance_starts_at_the_opening()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var request = new SavePartyRequest("Hari Traders", "Supplier", Phone: "9800000000",
            OpeningBalance: 1500.50m, OpeningSide: "ToGive", Id: Guid.CreateVersion7());

        var first = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties", request);
        var retry = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var party = await first.Content.ReadFromJsonAsync<PartyDto>();
        Assert.Equal(request.Id, party!.Id);
        Assert.Equal(-1500.50m, party.Balance); // To Give is negative
        Assert.Equal("ToGive", party.OpeningSide);
        Assert.NotNull(party.OpeningAsOf);

        var list = await owner.Http.GetFromJsonAsync<List<PartyDto>>($"/api/companies/{companyId}/parties");
        Assert.Single(list!, p => p.Name == "Hari Traders");

        var ledger = await owner.Http.GetFromJsonAsync<LedgerDto>($"/api/companies/{companyId}/parties/{party.Id}/ledger");
        Assert.Empty(ledger!.Lines);
        Assert.Equal(-1500.50m, ledger.ClosingBalance);
    }

    [PostgresFact]
    public async Task Party_rejects_bad_fields()
    {
        var (owner, companyId) = await api.NewCompanyAsync();

        var response = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties",
            new SavePartyRequest(" ", "Vendor", Email: "not-an-email", OpeningBalance: -5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("name", body);
        Assert.Contains("partyType", body);
        Assert.Contains("email", body);
        Assert.Contains("openingBalance", body);
    }

    [PostgresFact]
    public async Task Another_companys_party_is_not_found()
    {
        var (ownerA, companyA) = await api.NewCompanyAsync();
        var (ownerB, companyB) = await api.NewCompanyAsync();
        var partyB = await CreatePartyAsync(ownerB, companyB, "B's customer");

        var url = $"/api/companies/{companyA}/parties/{partyB.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.Http.GetAsync(url + "/ledger")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.Http.PutAsJsonAsync(url, new SavePartyRequest("Hijack", "Customer"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.Http.DeleteAsync(url)).StatusCode);

        // Re-using B's id to create a party in A is refused rather than treated as a retry.
        var reuse = await ownerA.Http.PostAsJsonAsync($"/api/companies/{companyA}/parties", new SavePartyRequest("Copy", "Customer", Id: partyB.Id));
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.Equal("B's customer", (await ownerB.Http.GetFromJsonAsync<PartyDto>($"/api/companies/{companyB}/parties/{partyB.Id}"))!.Name);
    }

    [PostgresFact]
    public async Task Entry_person_adds_parties_dated_today_but_cannot_edit_or_delete_them()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");
        var parties = $"/api/companies/{companyId}/parties";

        var added = await clerk.Http.PostAsJsonAsync(parties, new SavePartyRequest("Walk-in", "Customer", OpeningBalance: 200));
        var backDated = await clerk.Http.PostAsJsonAsync(parties, new SavePartyRequest("Old debt", "Customer",
            OpeningBalance: 200, OpeningAsOf: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30)));

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, backDated.StatusCode);

        var party = (await added.Content.ReadFromJsonAsync<PartyDto>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PutAsJsonAsync($"{parties}/{party.Id}", new SavePartyRequest("Renamed", "Customer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.DeleteAsync($"{parties}/{party.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clerk.Http.GetAsync($"{parties}/{party.Id}")).StatusCode);
    }

    [PostgresFact]
    public async Task Lock_dated_user_can_change_an_opening_amount_which_is_then_dated_today()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var parties = $"/api/companies/{companyId}/parties";
        var created = await owner.Http.PostAsJsonAsync(parties, new SavePartyRequest("Old Customer", "Customer",
            OpeningBalance: 100, OpeningAsOf: new DateOnly(2026, 1, 1)));
        var party = (await created.Content.ReadFromJsonAsync<PartyDto>())!;

        // An accountant whose entries are locked to today (the form sends no date then).
        var staff = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, staff, "Accountant",
            [.. RoleTemplates.For(CompanyRole.Accountant), Permission.RestrictLockDate]);

        var rename = await staff.Http.PutAsJsonAsync($"{parties}/{party.Id}", new SavePartyRequest("Old Customer Ltd", "Customer", OpeningBalance: 100));
        Assert.Equal(new DateOnly(2026, 1, 1), (await rename.Content.ReadFromJsonAsync<PartyDto>())!.OpeningAsOf); // untouched opening keeps its date

        var newAmount = await staff.Http.PutAsJsonAsync($"{parties}/{party.Id}", new SavePartyRequest("Old Customer Ltd", "Customer", OpeningBalance: 250));
        Assert.Equal(HttpStatusCode.OK, newAmount.StatusCode);
        Assert.Equal(CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System), (await newAmount.Content.ReadFromJsonAsync<PartyDto>())!.OpeningAsOf);

        var backDated = await staff.Http.PutAsJsonAsync($"{parties}/{party.Id}", new SavePartyRequest("Old Customer Ltd", "Customer",
            OpeningBalance: 300, OpeningAsOf: new DateOnly(2026, 1, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, backDated.StatusCode);
    }

    [PostgresFact]
    public async Task Party_permissions_follow_the_party_type()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var customer = await CreatePartyAsync(owner, companyId, "Customer One");
        var supplier = await CreatePartyAsync(owner, companyId, "Supplier One", "Supplier");

        // An accountant who can't see or touch suppliers.
        var staff = await api.NewUserAsync();
        var noSuppliers = RoleTemplates.For(CompanyRole.Accountant).Where(p => !p.StartsWith("parties.suppliers.", StringComparison.Ordinal)).ToList();
        await ApiFixture.AddMemberAsync(owner, companyId, staff, "Accountant", noSuppliers);
        var parties = $"/api/companies/{companyId}/parties";

        var visible = await staff.Http.GetFromJsonAsync<List<PartyDto>>(parties);
        Assert.Equal(["Customer One"], visible!.Select(p => p.Name));
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.Http.GetAsync($"{parties}/{supplier.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.Http.PostAsJsonAsync(parties, new SavePartyRequest("New supplier", "Supplier"))).StatusCode);

        // Turning a customer into a supplier needs "add suppliers".
        var toSupplier = await staff.Http.PutAsJsonAsync($"{parties}/{customer.Id}", new SavePartyRequest("Customer One", "Supplier"));
        Assert.Equal(HttpStatusCode.Forbidden, toSupplier.StatusCode);
        var rename = await staff.Http.PutAsJsonAsync($"{parties}/{customer.Id}", new SavePartyRequest("Customer Uno", "Customer"));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [PostgresFact]
    public async Task Party_categories_are_listed_and_must_belong_to_the_company()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var (_, otherCompany) = await api.NewCompanyAsync();
        var categories = $"/api/companies/{companyId}/party-categories";

        var created = await owner.Http.PostAsJsonAsync(categories, new SavePartyCategoryRequest("Wholesale"));
        var duplicate = await owner.Http.PostAsJsonAsync(categories, new SavePartyCategoryRequest("wholesale"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var category = (await created.Content.ReadFromJsonAsync<PartyCategoryDto>())!;

        var party = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties",
            new SavePartyRequest("Big Buyer", "Customer", CategoryId: category.Id));
        Assert.Equal("Wholesale", (await party.Content.ReadFromJsonAsync<PartyDto>())!.CategoryName);

        var foreign = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties",
            new SavePartyRequest("Odd", "Customer", CategoryId: Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Http.GetAsync($"/api/companies/{otherCompany}/party-categories")).StatusCode);
    }

    // ---------------- Money accounts ----------------

    [PostgresFact]
    public async Task Money_accounts_list_only_cash_bank_cheque_and_wallet()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var accounts = $"/api/companies/{companyId}/accounts";

        var seeded = await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>(accounts);
        Assert.Equal(["Cash", "Online Wallet", "Cheque"], seeded!.Select(a => a.Name));

        var bank = await owner.Http.PostAsJsonAsync(accounts, new SaveMoneyAccountRequest(
            "Nabil Bank", "Bank", "Ram Kirana", "0123456789", OpeningBalance: -1000m));
        var petty = await owner.Http.PostAsJsonAsync(accounts, new SaveMoneyAccountRequest(
            "Petty Cash", "Cash", "ignored", "ignored", OpeningBalance: 500m));
        var cheque = await owner.Http.PostAsJsonAsync(accounts, new SaveMoneyAccountRequest("Another Cheque", "Cheque"));

        Assert.Equal(HttpStatusCode.Created, bank.StatusCode);
        Assert.Equal(HttpStatusCode.Created, petty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, cheque.StatusCode);
        var bankDto = (await bank.Content.ReadFromJsonAsync<MoneyAccountDto>())!;
        Assert.Equal(-1000m, bankDto.Balance); // overdrawn
        var pettyDto = (await petty.Content.ReadFromJsonAsync<MoneyAccountDto>())!;
        Assert.Null(pettyDto.AccountNumber); // cash has no bank details

        var renamed = await owner.Http.PutAsJsonAsync($"{accounts}/{bankDto.Id}",
            new SaveMoneyAccountRequest("Nabil Bank Ltd", "Bank", "Ram Kirana", "0123456789", OpeningBalance: -1000m));
        Assert.Equal("Nabil Bank Ltd", (await renamed.Content.ReadFromJsonAsync<MoneyAccountDto>())!.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"{accounts}/{bankDto.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"{accounts}/{bankDto.Id}")).StatusCode);

        // Parties and income/expense heads aren't money accounts.
        var party = await CreatePartyAsync(owner, companyId, "Not money");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"{accounts}/{party.Id}")).StatusCode);
    }

    [PostgresFact]
    public async Task Built_in_accounts_and_accounts_with_transactions_cannot_be_deleted()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var accounts = $"/api/companies/{companyId}/accounts";
        var cash = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>(accounts))!.Single(a => a.Name == "Cash");
        var bank = (await (await owner.Http.PostAsJsonAsync(accounts, new SaveMoneyAccountRequest("Bank", "Bank")))
            .Content.ReadFromJsonAsync<MoneyAccountDto>())!;
        await AddTransactionAsync(companyId, TransactionType.TransferMoney, from: cash.Id, to: bank.Id, 2500m);

        Assert.Equal(HttpStatusCode.Conflict, (await owner.Http.DeleteAsync($"{accounts}/{cash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Http.DeleteAsync($"{accounts}/{bank.Id}")).StatusCode);

        var ledger = await owner.Http.GetFromJsonAsync<LedgerDto>($"{accounts}/{bank.Id}/ledger");
        var line = Assert.Single(ledger!.Lines);
        Assert.Equal(2500m, line.Debit);
        Assert.Equal("Cash", line.OtherAccount);
        Assert.Equal(2500m, ledger.ClosingBalance);
        Assert.Equal(-2500m, (await owner.Http.GetFromJsonAsync<MoneyAccountDto>($"{accounts}/{cash.Id}"))!.Balance);
    }

    [PostgresFact]
    public async Task Manager_sees_accounts_but_cannot_add_or_edit_them()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var manager = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, manager, "Manager");
        var accounts = $"/api/companies/{companyId}/accounts";
        var cash = (await manager.Http.GetFromJsonAsync<List<MoneyAccountDto>>(accounts))!.Single(a => a.Name == "Cash");

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Http.PostAsJsonAsync(accounts, new SaveMoneyAccountRequest("Bank", "Bank"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Http.PutAsJsonAsync($"{accounts}/{cash.Id}", new SaveMoneyAccountRequest("Till", "Cash"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Http.DeleteAsync($"{accounts}/{cash.Id}")).StatusCode);
    }

    // ---------------- Server versions and audit ----------------

    [PostgresFact]
    public async Task Every_write_gets_a_new_server_version_and_an_audit_row()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        using (var scope = api.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
            var seeded = await db.Accounts.Where(a => a.CompanyId == companyId).Select(a => a.Version).ToListAsync();
            Assert.DoesNotContain(0L, seeded);
            Assert.Equal(seeded.Count, seeded.Distinct().Count());
            Assert.True(await db.CompanySyncStates.SingleAsync(s => s.CompanyId == companyId) is var state && state.LastVersion > seeded.Max());
        }

        var before = await LastVersionAsync(companyId);
        var party = await CreatePartyAsync(owner, companyId, "Versioned");
        (await owner.Http.PutAsJsonAsync($"/api/companies/{companyId}/parties/{party.Id}", new SavePartyRequest("Versioned 2", "Customer")))
            .EnsureSuccessStatusCode();

        using var check = api.Factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var row = await checkDb.Accounts.SingleAsync(a => a.Id == party.Id);
        Assert.Equal(before + 2, row.Version);
        Assert.Equal(before + 2, await LastVersionAsync(companyId));
        Assert.NotNull(row.CreatedBy);
        var actions = await checkDb.AuditLogs.Where(a => a.EntityId == party.Id).OrderBy(a => a.AtUtc).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Create", "Update"], actions);
    }

    // ---------------- Helpers ----------------

    private static async Task<PartyDto> CreatePartyAsync(ApiFixture.UserClient owner, Guid companyId, string name, string type = "Customer")
    {
        var response = await owner.Http.PostAsJsonAsync($"/api/companies/{companyId}/parties", new SavePartyRequest(name, type));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PartyDto>())!;
    }

    /// <summary>Transactions get their own endpoints in phase 6; until then tests insert them directly.</summary>
    private async Task AddTransactionAsync(Guid companyId, TransactionType type, Guid from, Guid to, decimal amount)
    {
        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var now = DateTime.UtcNow;
        db.Transactions.Add(new Transaction
        {
            CompanyId = companyId,
            Type = type,
            VoucherNo = "T-1",
            Date = DateOnly.FromDateTime(now),
            Amount = amount,
            FromAccountId = from,
            ToAccountId = to,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> LastVersionAsync(Guid companyId)
    {
        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        return await db.CompanySyncStates.Where(s => s.CompanyId == companyId).Select(s => s.LastVersion).SingleAsync();
    }
}
