using System.Net;
using System.Net.Http.Json;
using LekhaBook.Contracts;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>The company calendar (BS or AD): chosen with business settings, sent to every member.</summary>
public sealed class CompanyCalendarTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [PostgresFact]
    public async Task Owner_switches_the_calendar_and_members_see_it()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");
        Assert.Equal("BS", await CalendarOfAsync(clerk.Http, companyId));

        var url = $"/api/companies/{companyId}/calendar";
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PutAsJsonAsync(url, new UpdateCalendarRequest("AD"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Http.PutAsJsonAsync(url, new UpdateCalendarRequest("Julian"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsJsonAsync(url, new UpdateCalendarRequest("AD"))).StatusCode);

        Assert.Equal("AD", await CalendarOfAsync(clerk.Http, companyId));
    }

    [PostgresFact]
    public async Task A_company_can_be_created_with_the_ad_calendar()
    {
        var owner = await api.NewUserAsync();

        var created = await owner.Http.PostAsJsonAsync("/api/companies", new CreateCompanyRequest("Overseas Traders", Calendar: "AD"));
        var company = await created.Content.ReadFromJsonAsync<CompanySummary>();

        Assert.Equal("AD", company!.Calendar);
        Assert.Equal("AD", await CalendarOfAsync(owner.Http, company.CompanyId));
    }

    private static async Task<string> CalendarOfAsync(HttpClient http, Guid companyId)
    {
        var me = await http.GetFromJsonAsync<MeResponse>("/api/me");
        return me!.Companies.Single(c => c.CompanyId == companyId).Calendar;
    }
}
