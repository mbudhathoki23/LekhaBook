using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Public report links (D24): who may make them, what a holder sees, and when they stop working.</summary>
public sealed class ReportLinkTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    [PostgresFact]
    public async Task A_link_shows_the_statement_and_its_photos_without_signing_in()
    {
        var books = await SetUpAsync();
        var created = await CreateAsync(books, books.Owner, books.Party);

        Assert.StartsWith("https://books.example.test/r/", created.ReportUrl);
        Assert.Equal("https://books.example.test/signin", created.SignInUrl);

        var response = await GetPublicAsync("/api/public/report", Token(created));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var report = await response.Content.ReadFromJsonAsync<PublicReportDto>();
        Assert.Equal("Sita Traders", report!.Name);
        Assert.Equal("Pokhara", report.Address);
        Assert.Equal(400m, report.ClosingBalance); // 500 to receive, 100 paid
        var line = Assert.Single(report.Lines);
        Assert.Equal("", line.VoucherNo);
        var file = Assert.Single(line.Files);

        // The party's phone and email are never published.
        var json = await (await GetPublicAsync("/api/public/report", Token(created))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("9801234567", json);
        Assert.DoesNotContain("sita@example.test", json);

        var download = await GetPublicAsync($"/api/public/report/files/{file.Id}", Token(created));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Photo, await download.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task A_bank_statement_can_be_shared_with_its_number_masked()
    {
        var books = await SetUpAsync();
        var bank = await PostAsync<MoneyAccountDto>(books.Owner, $"{books.Url}/accounts", new SaveMoneyAccountRequest(
            "Nabil Bank", "Bank", AccountHolderName: "Test Kirana", AccountNumber: "0123456789", OpeningBalance: 900));
        var created = await CreateAsync(books, books.Owner, bank.Id);

        var report = await ReadAsync(Token(created));
        Assert.Equal("Bank", report.AccountType);
        Assert.Null(report.PartyType);
        Assert.Null(report.Address);
        Assert.Null(report.Pan);
        Assert.Equal("•••• 6789", report.AccountNumber);
        Assert.Equal(900m, report.ClosingBalance);
        Assert.Equal(report.ExpiresOn, created.Link.ExpiresOn);

        // An Entry Person sees accounts but can't export them.
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, clerk, "EntryPerson");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await clerk.Http.PostAsJsonAsync($"{books.Url}/report-links", new CreateReportLinkRequest(bank.Id))).StatusCode);
    }

    [PostgresFact]
    public async Task A_link_without_photos_shows_none_and_serves_none()
    {
        var books = await SetUpAsync();
        var created = await CreateAsync(books, books.Owner, books.Party, includeFiles: false);

        var report = await ReadAsync(Token(created));
        Assert.Empty(Assert.Single(report.Lines).Files);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await GetPublicAsync($"/api/public/report/files/{books.AttachmentId}", Token(created))).StatusCode);
    }

    [PostgresFact]
    public async Task A_link_only_serves_files_of_its_own_statement()
    {
        var books = await SetUpAsync();
        var other = await AddPartyAsync(books, "Ram Store");
        var created = await CreateAsync(books, books.Owner, other);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await GetPublicAsync($"/api/public/report/files/{books.AttachmentId}", Token(created))).StatusCode);
    }

    [PostgresFact]
    public async Task Stopped_expired_and_unknown_links_are_not_found()
    {
        var books = await SetUpAsync();
        var stopped = await CreateAsync(books, books.Owner, books.Party);
        var expired = await CreateAsync(books, books.Owner, books.Party);

        Assert.Equal(HttpStatusCode.NoContent, (await books.Owner.Http.DeleteAsync($"{books.Url}/report-links/{stopped.Link.Id}")).StatusCode);
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
            await db.ReportShareLinks.Where(l => l.Id == expired.Link.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));

            // Only a hash is stored.
            Assert.DoesNotContain(await db.ReportShareLinks.Select(l => l.TokenHash).ToListAsync(), h => h == Token(stopped));
        }

        foreach (var token in new[] { Token(stopped), Token(expired), "nonsense", "" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetPublicAsync("/api/public/report", token)).StatusCode);
        }

        var live = await books.Owner.Http.GetFromJsonAsync<List<ReportLinkDto>>($"{books.Url}/report-links?accountId={books.Party}");
        Assert.Empty(live!);
    }

    [PostgresFact]
    public async Task No_link_is_made_until_the_public_site_is_set()
    {
        var books = await SetUpAsync();
        await using var unset = api.Factory.WithTestSettings(new() { ["PublicSite:BaseUrl"] = "" });
        var client = unset.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", books.Owner.Token.AccessToken);

        var response = await client.PostAsJsonAsync($"{books.Url}/report-links", new CreateReportLinkRequest(books.Party));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("public web address", await response.Content.ReadAsStringAsync());
        var links = await books.Owner.Http.GetFromJsonAsync<List<ReportLinkDto>>($"{books.Url}/report-links?accountId={books.Party}");
        Assert.Empty(links!);
    }

    [PostgresFact]
    public async Task Sharing_needs_export_rights_and_membership()
    {
        var books = await SetUpAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, clerk, "EntryPerson");
        var outsider = await api.NewUserAsync();
        var request = new CreateReportLinkRequest(books.Party);

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PostAsJsonAsync($"{books.Url}/report-links", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.PostAsJsonAsync($"{books.Url}/report-links", request)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await books.Owner.Http.PostAsJsonAsync($"{books.Url}/report-links", request with { ExpiresInDays = 91 })).StatusCode);

        // Another company's party can't be shared through this company.
        var elsewhere = await SetUpAsync();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await books.Owner.Http.PostAsJsonAsync($"{books.Url}/report-links", new CreateReportLinkRequest(elsewhere.Party))).StatusCode);
    }

    [PostgresFact]
    public async Task A_link_stops_working_when_its_creator_loses_access()
    {
        var books = await SetUpAsync();
        var partner = await api.NewUserAsync();
        var member = await ApiFixture.AddMemberAsync(books.Owner, books.CompanyId, partner, "Partner");
        var created = await CreateAsync(books, partner, books.Party);
        Assert.Equal(HttpStatusCode.OK, (await GetPublicAsync("/api/public/report", Token(created))).StatusCode);

        (await books.Owner.Http.DeleteAsync($"{books.Url}/members/{member.MemberId}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await GetPublicAsync("/api/public/report", Token(created))).StatusCode);
    }

    [PostgresFact]
    public async Task Only_a_member_who_may_see_the_statement_can_open_it_in_the_app()
    {
        var books = await SetUpAsync();
        var created = await CreateAsync(books, books.Owner, books.Party);
        var request = new ResolveReportLinkRequest(Token(created));

        var mine = await books.Owner.Http.PostAsJsonAsync("/api/report-links/resolve", request);
        Assert.Equal(new ResolvedReportLinkDto(books.CompanyId, books.Party, true), await mine.Content.ReadFromJsonAsync<ResolvedReportLinkDto>());

        var outsider = await api.NewUserAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.PostAsJsonAsync("/api/report-links/resolve", request)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await api.Factory.CreateClient().PostAsJsonAsync("/api/report-links/resolve", request)).StatusCode);
    }

    private sealed record Books(ApiFixture.UserClient Owner, Guid CompanyId, Guid Party, Guid Cash, Guid AttachmentId)
    {
        public string Url => $"/api/companies/{CompanyId}";
    }

    /// <summary>A customer who owed 500, paid 100 in cash, with a photo of the receipt.</summary>
    private async Task<Books> SetUpAsync()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";
        var party = await PostAsync<PartyDto>(owner, $"{url}/parties", new SavePartyRequest(
            "Sita Traders", "Customer", Phone: "9801234567", Email: "sita@example.test", Address: "Pokhara", OpeningBalance: 500));
        var cash = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!.First(a => a.Type == "Cash").Id;
        var entry = await PostAsync<TransactionDto>(owner, $"{url}/transactions",
            new CreateTransactionRequest("PaymentIn", null, 100m, party.Id, cash, Id: Guid.CreateVersion7()));

        var file = new ByteArrayContent(Photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var form = new MultipartFormDataContent { { file, "file", "receipt.jpg" } };
        form.Add(new StringContent(entry.Id.ToString()), "transactionId");
        var added = await owner.Http.PostAsync($"{url}/attachments", form);
        added.EnsureSuccessStatusCode();
        var attachment = await added.Content.ReadFromJsonAsync<AttachmentDto>();

        return new Books(owner, companyId, party.Id, cash, attachment!.Id);
    }

    private static async Task<Guid> AddPartyAsync(Books books, string name) =>
        (await PostAsync<PartyDto>(books.Owner, $"{books.Url}/parties", new SavePartyRequest(name, "Customer"))).Id;

    private static async Task<T> PostAsync<T>(ApiFixture.UserClient user, string url, object body)
    {
        var response = await user.Http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<CreatedReportLinkDto> CreateAsync(Books books, ApiFixture.UserClient user, Guid accountId, bool includeFiles = true) =>
        PostAsync<CreatedReportLinkDto>(user, $"{books.Url}/report-links", new CreateReportLinkRequest(accountId, IncludeFiles: includeFiles));

    private static string Token(CreatedReportLinkDto created) => created.ReportUrl[(created.ReportUrl.LastIndexOf('/') + 1)..];

    private async Task<PublicReportDto> ReadAsync(string token) =>
        (await (await GetPublicAsync("/api/public/report", token)).Content.ReadFromJsonAsync<PublicReportDto>())!;

    private async Task<HttpResponseMessage> GetPublicAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ReportLinkHeaders.Token, token);
        return await api.Factory.CreateClient().SendAsync(request);
    }
}
