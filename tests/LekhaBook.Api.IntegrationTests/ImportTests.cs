using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using LekhaBook.Application.Import;
using LekhaBook.Contracts;
using LekhaBook.Domain;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>An import from file to balances, the way the screen does it (D26).</summary>
public sealed class ImportTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static DateOnly Today => CompanyClock.Today(CompanyClock.DefaultTimeZoneId, TimeProvider.System);

    [PostgresFact]
    public async Task Imported_parties_and_entries_give_the_expected_balances_and_a_second_import_adds_nothing()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";

        // Customer owes 1,000; supplier is owed 500.
        var parties = await ImportAsync(owner.Http, url, ImportKind.Parties,
            "Name,Mobile,To Receive,To Give,Type\nImport Customer,9800000001,\"1,000.00\",,Customer\nImport Supplier,,,500,Supplier\n");
        Assert.Equal(2, parties.Rows.Count(r => r.Saved));

        const string entries =
            "Date,Type,Party,Amount,Payment Method,Category\n" +
            "{0},Payment In,Import Customer,400,Cash,\n" +
            "{0},Payment Out,Import Supplier,300,Cash,\n" +
            "{0},Expense,,150.25,Cash,Fuel\n" +
            "{0},Payment In,Walk In Buyer,75,Cash,\n";
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, entries, Today.ToString("yyyy-MM-dd"));
        var first = await ImportAsync(owner.Http, url, ImportKind.Entries, text);
        Assert.All(first.Rows, r => Assert.True(r.Saved, r.SaveError ?? r.Note?.Key));

        var balances = (await owner.Http.GetFromJsonAsync<List<PartyDto>>($"{url}/parties"))!.ToDictionary(p => p.Name, p => p.Balance);
        Assert.Equal(600m, balances["Import Customer"]);
        Assert.Equal(-200m, balances["Import Supplier"]);
        Assert.Equal(-75m, balances["Walk In Buyer"]);
        var cash = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!.Single(a => a.Name == "Cash");
        Assert.Equal(24.75m, cash.Balance);

        // The same files again: everything is recognised and nothing is selected.
        var again = await ImportAsync(owner.Http, url, ImportKind.Entries, text);
        Assert.All(again.Rows, r => Assert.Equal(ImportRowStatus.Duplicate, r.Status));
        Assert.Empty(again.NewParties);
        Assert.Equal(4, (await owner.Http.GetFromJsonAsync<List<TransactionDto>>($"{url}/transactions"))!.Count);
    }

    [PostgresFact]
    public async Task Only_members_who_can_add_something_may_have_a_file_read()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var viewer = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, viewer, "Accountant", [Permission.CustomersView]);
        var url = $"/api/companies/{companyId}/import/preview";

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsync(url, Upload("a,b\n1,2"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PostAsync(url, Upload("a,b\n1,2"))).StatusCode);

        var tooLarge = await owner.Http.PostAsync(url, Upload(new string('a', ImportLimits.MaxFileBytes + 1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("5 MB", await tooLarge.Content.ReadAsStringAsync());
    }

    private static MultipartFormDataContent Upload(string text)
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "import.csv" } };
    }

    /// <summary>Reads the file with the server, plans it against the books and saves the selected rows.</summary>
    private static async Task<ImportPlan> ImportAsync(HttpClient http, string url, ImportKind kind, string text)
    {
        var response = await http.PostAsync($"{url}/import/preview", Upload(text));
        response.EnsureSuccessStatusCode();
        var rows = (await response.Content.ReadFromJsonAsync<ImportFileDto>())!.Tables[0].Rows;

        var accounts = (await http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!;
        var categories = (await http.GetFromJsonAsync<List<CategoryDto>>($"{url}/categories"))!;
        var books = new ImportBooks(
            (await http.GetFromJsonAsync<List<PartyDto>>($"{url}/parties"))!,
            accounts,
            categories,
            (await http.GetFromJsonAsync<List<TransactionDto>>($"{url}/transactions"))!,
            Today);

        var header = ImportColumns.DetectHeaderRow(rows, kind);
        var plan = ImportPlanner.Build(rows, new ImportOptions
        {
            Kind = kind,
            HeaderRow = header,
            Columns = ImportColumns.Guess(rows[header], kind),
            DefaultMoneyAccountId = accounts.Single(a => a.Name == "Cash").Id,
            DefaultExpenseId = categories.First(c => c.Type == "Expense").Id,
            DefaultIncomeId = categories.First(c => c.Type == "Income").Id,
        }, books);

        static async Task<ImportReply> Reply(Task<HttpResponseMessage> send)
        {
            using var result = await send;
            return result.IsSuccessStatusCode ? new ImportReply() : new ImportReply(await result.Content.ReadAsStringAsync());
        }

        Assert.True(await ImportRunner.RunAsync(
            plan,
            party => Reply(http.PostAsJsonAsync($"{url}/parties", party)),
            entry => Reply(http.PostAsJsonAsync($"{url}/transactions", entry)),
            () => { }));
        return plan;
    }
}
