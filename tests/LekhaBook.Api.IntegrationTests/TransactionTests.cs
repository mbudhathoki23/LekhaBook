using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using LekhaBook.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Transactions and attachments through the API (phase 6, D15).</summary>
public sealed class TransactionTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static DateOnly Today => CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);

    // ---------------- Hand-calculated sheet (phase 6 exit criterion) ----------------

    /// <summary>
    /// Opening: customer owes 1,000.00; supplier is owed 500.00; Cash 2,000.00; Bank 10,000.00.
    ///   1  Payment In      customer → Cash      400.00
    ///   2  Payment Out     Bank → supplier      300.00
    ///   3  Expense         Cash → Fuel          150.25
    ///   4  Other Income    Commission → Bank     75.50
    ///   5  Transfer        Cash → Bank          500.00
    /// Customer 600.00 · Supplier −200.00 · Cash 1,749.75 · Bank 10,275.50 · Fuel 150.25
    /// </summary>
    [PostgresFact]
    public async Task Balances_match_a_hand_calculated_sheet_and_follow_edits_and_voids()
    {
        var books = await SetUpAsync();

        var paymentIn = await books.AddAsync("PaymentIn", books.Customer, books.Cash, 400m);
        await books.AddAsync("PaymentOut", books.Bank, books.Supplier, 300m);
        await books.AddAsync("Expense", books.Cash, books.Fuel, 150.25m);
        await books.AddAsync("OtherIncome", books.Commission, books.Bank, 75.50m);
        var transfer = await books.AddAsync("TransferMoney", books.Cash, books.Bank, 500m);

        Assert.Equal(600m, await books.PartyBalanceAsync(books.Customer));
        Assert.Equal(-200m, await books.PartyBalanceAsync(books.Supplier));
        Assert.Equal(1749.75m, await books.MoneyBalanceAsync(books.Cash));
        Assert.Equal(10275.50m, await books.MoneyBalanceAsync(books.Bank));

        var ledger = await books.Owner.Http.GetFromJsonAsync<LedgerDto>($"{books.Url}/accounts/{books.Cash}/ledger");
        Assert.Equal(3, ledger!.Lines.Count);
        Assert.Equal(1749.75m, ledger.ClosingBalance);

        // Edit: the Payment In was really 450.00 and had no remarks.
        var edited = await books.Owner.Http.PutAsJsonAsync($"{books.Url}/transactions/{paymentIn.Id}",
            new UpdateTransactionRequest(paymentIn.Date, 450m, books.Customer, books.Cash));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Null((await edited.Content.ReadFromJsonAsync<TransactionDto>())!.Remarks);

        // Void the transfer.
        Assert.Equal(HttpStatusCode.NoContent, (await books.Owner.Http.DeleteAsync($"{books.Url}/transactions/{transfer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await books.Owner.Http.DeleteAsync($"{books.Url}/transactions/{transfer.Id}")).StatusCode);

        Assert.Equal(550m, await books.PartyBalanceAsync(books.Customer));
        Assert.Equal(2299.75m, await books.MoneyBalanceAsync(books.Cash)); // 2000 + 450 − 150.25
        Assert.Equal(9775.50m, await books.MoneyBalanceAsync(books.Bank)); // 10000 − 300 + 75.50

        var list = await books.Owner.Http.GetFromJsonAsync<List<TransactionDto>>($"{books.Url}/transactions");
        Assert.Equal(4, list!.Count);
        var voided = await books.Owner.Http.GetFromJsonAsync<TransactionDto>($"{books.Url}/transactions/{transfer.Id}");
        Assert.Equal("Void", voided!.Status);

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var audit = await db.AuditLogs.Where(a => a.EntityId == paymentIn.Id || a.EntityId == transfer.Id)
            .OrderBy(a => a.AtUtc).ToListAsync();
        Assert.Equal(["Create", "Create", "Update", "Void"], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.NotNull(a.UserId));
        var versions = await db.Transactions.Where(t => t.CompanyId == books.CompanyId).Select(t => t.Version).ToListAsync();
        Assert.DoesNotContain(0L, versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    // ---------------- Posting rules and tenant isolation ----------------

    [PostgresFact]
    public async Task Entries_that_break_the_posting_rules_are_rejected()
    {
        var books = await SetUpAsync();
        var other = await SetUpAsync();

        async Task Rejected(string type, Guid from, Guid to, decimal amount = 10m)
        {
            var response = await books.PostAsync(new CreateTransactionRequest(type, null, amount, from, to));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await Rejected("TransferMoney", books.Cash, books.Cash);
        await Rejected("PaymentIn", books.Bank, books.Cash);          // no party
        await Rejected("Expense", books.Cash, books.Commission);      // income head as an expense
        await Rejected("TransferMoney", books.Cash, books.Bank, 1.005m);
        await Rejected("TransferMoney", books.Cash, books.Bank, -5m);
        await Rejected("Nonsense", books.Cash, books.Bank);
        await Rejected("2", books.Cash, books.Bank);
        await Rejected("TransferMoney", Guid.CreateVersion7(), books.Bank);
        await Rejected("TransferMoney", other.Cash, books.Bank);      // another company's account
        await Rejected("PaymentIn", other.Customer, books.Cash);

        var future = await books.PostAsync(new CreateTransactionRequest("TransferMoney", Today.AddDays(1), 10m, books.Cash, books.Bank));
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        var longCheque = await books.PostAsync(new CreateTransactionRequest(
            "TransferMoney", null, 10m, books.Cash, books.Bank, ChequeNo: new string('9', 41)));
        Assert.Equal(HttpStatusCode.BadRequest, longCheque.StatusCode);

        // Editing can't smuggle in another company's account or a new type either.
        var entry = await books.AddAsync("TransferMoney", books.Cash, books.Bank, 10m);
        var url = $"{books.Url}/transactions/{entry.Id}";
        Assert.Equal(HttpStatusCode.BadRequest, (await books.Owner.Http.PutAsJsonAsync(url,
            new UpdateTransactionRequest(Today, 10m, other.Cash, books.Bank))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await books.Owner.Http.PutAsJsonAsync(url,
            new UpdateTransactionRequest(Today, 10m, books.Cash, books.Bank, Type: "Expense"))).StatusCode);
        Assert.Equal(1990m, await books.MoneyBalanceAsync(books.Cash));
    }

    [PostgresFact]
    public async Task Another_companys_transaction_is_not_found()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        var entryB = await b.AddAsync("TransferMoney", b.Cash, b.Bank, 10m);

        var url = $"{a.Url}/transactions/{entryB.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.Http.PutAsJsonAsync(url, new UpdateTransactionRequest(Today, 1m, a.Cash, a.Bank))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.Http.DeleteAsync(url)).StatusCode);

        var reuse = await a.PostAsync(new CreateTransactionRequest("TransferMoney", null, 10m, a.Cash, a.Bank, Id: entryB.Id));
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
    }

    [PostgresFact]
    public async Task A_retried_or_doubled_create_saves_one_entry()
    {
        var books = await SetUpAsync();
        var request = new CreateTransactionRequest("TransferMoney", null, 100m, books.Cash, books.Bank, Id: Guid.CreateVersion7());

        var together = await Task.WhenAll(books.PostAsync(request), books.PostAsync(request), books.PostAsync(request));
        var retry = await books.PostAsync(request);

        Assert.All(together, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1900m, await books.MoneyBalanceAsync(books.Cash));
    }

    // ---------------- Who may write ----------------

    [PostgresFact]
    public async Task Entry_person_adds_and_changes_only_their_own_entries_dated_today()
    {
        var books = await SetUpAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, clerk, "EntryPerson");
        var ownersEntry = await books.AddAsync("PaymentIn", books.Customer, books.Cash, 100m);

        var added = await clerk.Http.PostAsJsonAsync($"{books.Url}/transactions",
            new CreateTransactionRequest("PaymentIn", null, 50m, books.Customer, books.Cash));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var own = (await added.Content.ReadFromJsonAsync<TransactionDto>())!;
        Assert.Equal(Today, own.Date);

        var backDated = await clerk.Http.PostAsJsonAsync($"{books.Url}/transactions",
            new CreateTransactionRequest("PaymentIn", Today.AddDays(-1), 50m, books.Customer, books.Cash));
        Assert.Equal(HttpStatusCode.Forbidden, backDated.StatusCode);

        var list = await clerk.Http.GetFromJsonAsync<List<TransactionDto>>($"{books.Url}/transactions");
        Assert.Equal(2, list!.Count);

        var theirs = $"{books.Url}/transactions/{ownersEntry.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PutAsJsonAsync(theirs,
            new UpdateTransactionRequest(Today, 1m, books.Customer, books.Cash))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.DeleteAsync(theirs)).StatusCode);

        var mine = $"{books.Url}/transactions/{own.Id}";
        Assert.Equal(HttpStatusCode.OK, (await clerk.Http.PutAsJsonAsync(mine,
            new UpdateTransactionRequest(Today, 60m, books.Customer, books.Cash))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PutAsJsonAsync(mine,
            new UpdateTransactionRequest(Today.AddDays(-1), 60m, books.Customer, books.Cash))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await clerk.Http.DeleteAsync(mine)).StatusCode);
    }

    [PostgresFact]
    public async Task Owner_can_edit_and_void_a_back_dated_entry()
    {
        var books = await SetUpAsync();
        var response = await books.PostAsync(new CreateTransactionRequest("TransferMoney", Today.AddDays(-30), 10m, books.Cash, books.Bank));
        var entry = (await response.Content.ReadFromJsonAsync<TransactionDto>())!;
        var url = $"{books.Url}/transactions/{entry.Id}";

        Assert.Equal(HttpStatusCode.OK, (await books.Owner.Http.PutAsJsonAsync(url,
            new UpdateTransactionRequest(entry.Date, 20m, books.Cash, books.Bank))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await books.Owner.Http.DeleteAsync(url)).StatusCode);
    }

    [PostgresFact]
    public async Task Permissions_follow_the_type_of_entry()
    {
        var books = await SetUpAsync();
        await books.AddAsync("PaymentIn", books.Customer, books.Cash, 100m);
        await books.AddAsync("Expense", books.Cash, books.Fuel, 20m);

        var staff = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, staff, "Accountant",
            [Permission.IncomeExpenseView, Permission.IncomeExpenseCreate]);

        var list = await staff.Http.GetFromJsonAsync<List<TransactionDto>>($"{books.Url}/transactions");
        Assert.Equal(["Expense"], list!.Select(t => t.Type));
        var paymentIn = await staff.Http.PostAsJsonAsync($"{books.Url}/transactions",
            new CreateTransactionRequest("PaymentIn", null, 5m, books.Customer, books.Cash));
        Assert.Equal(HttpStatusCode.Forbidden, paymentIn.StatusCode);

        var heads = await staff.Http.GetFromJsonAsync<List<CategoryDto>>($"{books.Url}/categories");
        Assert.Contains(heads!, h => h.Name == "Fuel" && h.Type == "Expense");
        Assert.Contains(heads!, h => h.Name == "Commission" && h.Type == "Income");
    }

    // ---------------- Attachments ----------------

    [PostgresFact]
    public async Task Attachments_belong_to_an_entry_the_user_may_change_or_see()
    {
        var books = await SetUpAsync();
        var other = await SetUpAsync();
        var entry = await books.AddAsync("PaymentIn", books.Customer, books.Cash, 100m);
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, clerk, "EntryPerson");

        static MultipartFormDataContent Photo(Guid? transactionId)
        {
            var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var form = new MultipartFormDataContent { { file, "file", "receipt.jpg" } };
            if (transactionId is { } id)
            {
                form.Add(new StringContent(id.ToString()), "transactionId");
            }

            return form;
        }

        var added = await books.Owner.Http.PostAsync($"{books.Url}/attachments", Photo(entry.Id));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var attachment = (await added.Content.ReadFromJsonAsync<AttachmentDto>())!;

        Assert.Equal(HttpStatusCode.BadRequest, (await books.Owner.Http.PostAsync($"{books.Url}/attachments", Photo(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Owner.Http.PostAsync($"{other.Url}/attachments", Photo(entry.Id))).StatusCode);
        // The clerk can't add proof to the owner's entry, but may look at it.
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PostAsync($"{books.Url}/attachments", Photo(entry.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clerk.Http.GetAsync($"{books.Url}/attachments/{attachment.Id}")).StatusCode);
        Assert.Single((await clerk.Http.GetFromJsonAsync<List<AttachmentDto>>($"{books.Url}/attachments?transactionId={entry.Id}"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.Http.GetAsync($"{other.Url}/attachments/{attachment.Id}")).StatusCode);

        var noPayments = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, noPayments, "Accountant", [Permission.IncomeExpenseView]);
        Assert.Equal(HttpStatusCode.Forbidden, (await noPayments.Http.GetAsync($"{books.Url}/attachments/{attachment.Id}")).StatusCode);
    }

    // ---------------- Helpers ----------------

    private async Task<Books> SetUpAsync()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";

        async Task<Guid> Party(string name, string type, decimal opening, string side)
        {
            var response = await owner.Http.PostAsJsonAsync($"{url}/parties",
                new SavePartyRequest(name, type, OpeningBalance: opening, OpeningSide: side));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PartyDto>())!.Id;
        }

        var customer = await Party("Customer", "Customer", 1000m, "ToReceive");
        var supplier = await Party("Supplier", "Supplier", 500m, "ToGive");

        var accounts = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!;
        var cash = accounts.Single(a => a.Name == "Cash");
        (await owner.Http.PutAsJsonAsync($"{url}/accounts/{cash.Id}", new SaveMoneyAccountRequest("Cash", "Cash", OpeningBalance: 2000m)))
            .EnsureSuccessStatusCode();
        var bankResponse = await owner.Http.PostAsJsonAsync($"{url}/accounts", new SaveMoneyAccountRequest("Bank", "Bank", OpeningBalance: 10000m));
        bankResponse.EnsureSuccessStatusCode();
        var bank = (await bankResponse.Content.ReadFromJsonAsync<MoneyAccountDto>())!;

        var heads = (await owner.Http.GetFromJsonAsync<List<CategoryDto>>($"{url}/categories"))!;
        return new Books(owner, companyId, url, customer, supplier, cash.Id, bank.Id,
            heads.Single(h => h.Name == "Fuel").Id, heads.Single(h => h.Name == "Commission").Id);
    }

    private sealed record Books(
        ApiFixture.UserClient Owner, Guid CompanyId, string Url,
        Guid Customer, Guid Supplier, Guid Cash, Guid Bank, Guid Fuel, Guid Commission)
    {
        public Task<HttpResponseMessage> PostAsync(CreateTransactionRequest request) =>
            Owner.Http.PostAsJsonAsync($"{Url}/transactions", request);

        public async Task<TransactionDto> AddAsync(string type, Guid from, Guid to, decimal amount)
        {
            var response = await PostAsync(new CreateTransactionRequest(type, null, amount, from, to, "note"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<TransactionDto>())!;
        }

        public async Task<decimal> PartyBalanceAsync(Guid id) =>
            (await Owner.Http.GetFromJsonAsync<PartyDto>($"{Url}/parties/{id}"))!.Balance;

        public async Task<decimal> MoneyBalanceAsync(Guid id) =>
            (await Owner.Http.GetFromJsonAsync<MoneyAccountDto>($"{Url}/accounts/{id}"))!.Balance;
    }
}
