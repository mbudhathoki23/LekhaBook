using System.Net;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Balance adjustments and new expense or income categories.</summary>
public sealed class BookExtrasTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [PostgresFact]
    public async Task Adjusting_moves_a_party_and_a_money_balance_the_way_asked_and_once_only()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";
        var party = await PostAsync<PartyDto>(owner, $"{url}/parties", new SavePartyRequest("Sita", "Customer", OpeningBalance: 500));
        var cash = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!.First(a => a.Type == "Cash");

        var add = new AdjustBalanceRequest(true, 100m, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PostAsJsonAsync($"{url}/accounts/{party.Id}/adjustments", add)).StatusCode);
        // A retried save posts nothing new.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PostAsJsonAsync($"{url}/accounts/{party.Id}/adjustments", add)).StatusCode);
        (await owner.Http.PostAsJsonAsync($"{url}/accounts/{party.Id}/adjustments", new AdjustBalanceRequest(false, 40m, Guid.CreateVersion7())))
            .EnsureSuccessStatusCode();
        Assert.Equal(560m, (await owner.Http.GetFromJsonAsync<PartyDto>($"{url}/parties/{party.Id}"))!.Balance);

        (await owner.Http.PostAsJsonAsync($"{url}/accounts/{cash.Id}/adjustments", new AdjustBalanceRequest(true, 250m, Guid.CreateVersion7())))
            .EnsureSuccessStatusCode();
        (await owner.Http.PostAsJsonAsync($"{url}/accounts/{cash.Id}/adjustments", new AdjustBalanceRequest(false, 30m, Guid.CreateVersion7())))
            .EnsureSuccessStatusCode();
        Assert.Equal(220m, (await owner.Http.GetFromJsonAsync<MoneyAccountDto>($"{url}/accounts/{cash.Id}"))!.Balance);

        var ledger = await owner.Http.GetFromJsonAsync<LedgerDto>($"{url}/accounts/{cash.Id}/ledger");
        Assert.Equal(["AddMoney", "ReduceMoney"], ledger!.Lines.Select(l => l.Type));
    }

    [PostgresFact]
    public async Task Adjustments_need_a_valid_amount_a_real_account_and_membership()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";
        var cash = (await owner.Http.GetFromJsonAsync<List<MoneyAccountDto>>($"{url}/accounts"))!.First(a => a.Type == "Cash");
        var head = (await owner.Http.GetFromJsonAsync<List<CategoryDto>>($"{url}/categories"))!.First();
        var outsider = await api.NewUserAsync();
        var request = new AdjustBalanceRequest(true, 10m, Guid.CreateVersion7());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.Http.PostAsJsonAsync($"{url}/accounts/{cash.Id}/adjustments", request with { Amount = 0 })).StatusCode);
        // A category isn't something with a balance to adjust.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.PostAsJsonAsync($"{url}/accounts/{head.Id}/adjustments", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.PostAsJsonAsync($"{url}/accounts/{cash.Id}/adjustments", request)).StatusCode);
    }

    [PostgresFact]
    public async Task A_new_category_is_listed_and_names_are_not_repeated()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}";
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");
        var request = new SaveCategoryRequest("Electricity", "Expense", Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PostAsJsonAsync($"{url}/categories", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.Http.PostAsJsonAsync($"{url}/categories", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PostAsJsonAsync($"{url}/categories", request)).StatusCode); // retried
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await owner.Http.PostAsJsonAsync($"{url}/categories", request with { Name = "electricity", Id = Guid.CreateVersion7() })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.Http.PostAsJsonAsync($"{url}/categories", request with { Type = "Bank", Id = Guid.CreateVersion7() })).StatusCode);

        var heads = await owner.Http.GetFromJsonAsync<List<CategoryDto>>($"{url}/categories");
        Assert.Single(heads!, h => h.Name == "Electricity" && h.Type == "Expense");
    }

    private static async Task<T> PostAsync<T>(ApiFixture.UserClient user, string url, object body)
    {
        var response = await user.Http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
