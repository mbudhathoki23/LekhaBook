using System.Net;
using System.Net.Http.Json;
using System.Text;
using LekhaBook.Contracts;
using LekhaBook.Domain;
using LekhaBook.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Phase 4 exit checks: company access is enforced by the server.</summary>
public sealed class TenancyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [PostgresFact]
    public async Task New_user_creates_a_company_and_becomes_its_owner()
    {
        var (owner, companyId) = await api.NewCompanyAsync();

        var me = await owner.Http.GetFromJsonAsync<MeResponse>("/api/me");
        var company = Assert.Single(me!.Companies);
        Assert.Equal(companyId, company.CompanyId);
        Assert.Equal("Owner", company.Role);
        Assert.Contains(Permission.BusinessDelete, company.Permissions);

        var details = await owner.Http.GetFromJsonAsync<CompanyDetails>($"/api/companies/{companyId}");
        Assert.Equal("Asia/Kathmandu", details!.TimeZoneId);
        Assert.Equal("BS", details.Calendar);
    }

    [PostgresFact]
    public async Task Signed_out_requests_get_401()
    {
        var (_, companyId) = await api.NewCompanyAsync();

        var response = await api.Factory.CreateClient().GetAsync($"/api/companies/{companyId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Walks every endpoint whose route has {companyId}, so endpoints added later are covered too.
    /// A signed-in user who isn't a member must get 403 before the request body is even read.
    /// </summary>
    [PostgresFact]
    public async Task Non_member_gets_403_on_every_company_endpoint()
    {
        var (_, companyId) = await api.NewCompanyAsync();
        var intruder = await api.NewUserAsync();

        var endpoints = api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("{companyId", StringComparison.Ordinal) == true)
            .ToList();
        Assert.True(endpoints.Count >= 7, $"Expected the company endpoints to be mapped, found {endpoints.Count}.");

        foreach (var endpoint in endpoints)
        {
            var path = "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment =>
                string.Concat(segment.Parts.Select(part => part switch
                {
                    Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l => l.Content,
                    Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart { Name: "companyId" } => companyId.ToString(),
                    Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart => Guid.NewGuid().ToString(),
                    _ => string.Empty,
                }))));

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT")
                {
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                }

                var response = await intruder.Http.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}");
            }
        }
    }

    [PostgresFact]
    public async Task Revoked_member_loses_access()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var staff = await api.NewUserAsync();
        var member = await ApiFixture.AddMemberAsync(owner, companyId, staff, "Accountant");
        Assert.Equal(HttpStatusCode.OK, (await staff.Http.GetAsync($"/api/companies/{companyId}")).StatusCode);

        var revoke = await owner.Http.DeleteAsync($"/api/companies/{companyId}/members/{member.MemberId}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.Http.GetAsync($"/api/companies/{companyId}")).StatusCode);
        var me = await staff.Http.GetFromJsonAsync<MeResponse>("/api/me");
        Assert.Empty(me!.Companies);
    }

    [PostgresFact]
    public async Task Custom_permissions_override_the_role_template()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var partner = await api.NewUserAsync();
        // A Partner normally has staff.view; this one is customised without it.
        var custom = RoleTemplates.For(CompanyRole.Partner).Where(p => p != Permission.StaffView).ToList();
        await ApiFixture.AddMemberAsync(owner, companyId, partner, "Partner", custom);

        Assert.Equal(HttpStatusCode.OK, (await partner.Http.GetAsync($"/api/companies/{companyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await partner.Http.GetAsync($"/api/companies/{companyId}/members")).StatusCode);

        // Giving it back through a member update restores access.
        var members = await owner.Http.GetFromJsonAsync<List<MemberDto>>($"/api/companies/{companyId}/members");
        var target = members!.Single(m => m.Role == "Partner");
        var update = await owner.Http.PutAsJsonAsync(
            $"/api/companies/{companyId}/members/{target.MemberId}", new UpdateMemberRequest("Partner", null));
        update.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await partner.Http.GetAsync($"/api/companies/{companyId}/members")).StatusCode);
    }

    [PostgresFact]
    public async Task Roles_without_staff_permissions_cannot_see_or_invite_staff()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.GetAsync($"/api/companies/{companyId}/members")).StatusCode);
        var invite = await clerk.Http.PostAsJsonAsync(
            $"/api/companies/{companyId}/invitations", new InviteRequest("x@example.test", "X", "EntryPerson"));
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

        var me = await clerk.Http.GetFromJsonAsync<MeResponse>("/api/me");
        var membership = Assert.Single(me!.Companies);
        Assert.Contains(Permission.RestrictLockDate, membership.Permissions);
    }

    [PostgresFact]
    public async Task Staff_cannot_grant_more_than_they_have_or_touch_the_owner()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var partner = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, partner, "Partner");
        var members = await owner.Http.GetFromJsonAsync<List<MemberDto>>($"/api/companies/{companyId}/members");
        var ownerMember = members!.Single(m => m.Role == "Owner");

        var escalate = await partner.Http.PostAsJsonAsync($"/api/companies/{companyId}/invitations",
            new InviteRequest("y@example.test", "Y", "Accountant", [Permission.CustomersView, Permission.BusinessDelete]));
        var secondOwner = await partner.Http.PostAsJsonAsync($"/api/companies/{companyId}/invitations",
            new InviteRequest("z@example.test", "Z", "Owner"));
        var revokeOwner = await partner.Http.DeleteAsync($"/api/companies/{companyId}/members/{ownerMember.MemberId}");

        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, secondOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revokeOwner.StatusCode);
    }

    [PostgresFact]
    public async Task Invitation_works_once_and_only_for_the_invited_email()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var invited = await api.NewUserAsync();
        var stranger = await api.NewUserAsync();
        var response = await owner.Http.PostAsJsonAsync(
            $"/api/companies/{companyId}/invitations", new InviteRequest(invited.Email.ToUpperInvariant(), "Invited", "Manager"));
        var token = (await response.Content.ReadFromJsonAsync<InviteResponse>())!.Token;

        var wrongPerson = await stranger.Http.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        var rightPerson = await invited.Http.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        var again = await invited.Http.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        var forged = await invited.Http.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest("not-a-real-token"));

        Assert.Equal(HttpStatusCode.Forbidden, wrongPerson.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightPerson.StatusCode);
        Assert.Equal("Manager", (await rightPerson.Content.ReadFromJsonAsync<CompanySummary>())!.Role);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [PostgresFact]
    public async Task Token_refresh_is_not_rate_limited()
    {
        // Many phones on one carrier share an IP; refreshes must not be throttled with sign-ins.
        await using var strict = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithTestSettings(new()
            {
                ["ConnectionStrings:LekhaBook"] = api.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("LekhaBook"),
                ["Database:MigrateOnStartup"] = "false",
                ["RateLimits:AuthPerMinute"] = "1",
            });
        var user = await api.NewUserAsync();
        var client = strict.CreateClient();

        var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = user.Token.RefreshToken });
        first.EnsureSuccessStatusCode();
        var next = (await first.Content.ReadFromJsonAsync<ApiFixture.TokenResponse>())!.RefreshToken;
        var second = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = next });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Sign-in attempts from the same IP are limited.
        var login1 = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-1" });
        var login2 = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-1" });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, login1.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, login2.StatusCode);
    }

    [PostgresFact]
    public async Task New_company_is_seeded_with_default_accounts()
    {
        var (_, companyId) = await api.NewCompanyAsync();

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LekhaBook.Data.Postgres.PostgresDbContext>();
        var names = db.Accounts.Where(a => a.CompanyId == companyId).Select(a => a.Name).ToList();

        Assert.Contains("Cash", names);
        Assert.Contains(CompanySeeder.AdjustmentAccountName, names);
        Assert.Contains("Fuel", names);
        Assert.True(db.CompanySyncStates.Any(s => s.CompanyId == companyId));
    }
}
