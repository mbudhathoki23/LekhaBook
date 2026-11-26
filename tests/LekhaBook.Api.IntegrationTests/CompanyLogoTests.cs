using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LekhaBook.Contracts;
using LekhaBook.Testing;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>The company logo: every member can read it, only business settings can change it.</summary>
public sealed class CompanyLogoTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [PostgresFact]
    public async Task Owner_uploads_a_logo_that_members_can_read()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}/logo";
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");

        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync(url)).StatusCode);
        Assert.False((await owner.Http.GetFromJsonAsync<CompanyDetails>($"/api/companies/{companyId}"))!.HasLogo);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(Png, "image/png", "logo.png"))).StatusCode);

        var read = await clerk.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        Assert.True((await clerk.Http.GetFromJsonAsync<CompanyDetails>($"/api/companies/{companyId}"))!.HasLogo);
    }

    [PostgresFact]
    public async Task Members_without_business_settings_can_read_the_logo_but_not_change_it()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}/logo";
        var clerk = await api.NewUserAsync();
        await ApiFixture.AddMemberAsync(owner, companyId, clerk, "EntryPerson");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(Png, "image/png", "logo.png"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.PutAsync(url, Upload(Png, "image/png", "logo.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Http.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clerk.Http.GetAsync(url)).StatusCode);
    }

    [PostgresFact]
    public async Task Logo_must_be_a_jpeg_png_or_webp_of_at_most_2_MB()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}/logo";

        await AssertFileProblemAsync(await owner.Http.PutAsync(url, Upload([0x25, 0x50, 0x44, 0x46], "application/pdf", "logo.pdf")));
        await AssertFileProblemAsync(await owner.Http.PutAsync(url, Upload(new byte[2 * 1024 * 1024 + 1], "image/jpeg", "big.jpg")));
        await AssertFileProblemAsync(await owner.Http.PutAsJsonAsync(url, new { }));

        // The limit itself is allowed.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(new byte[2 * 1024 * 1024], "image/jpeg", "edge.jpg"))).StatusCode);
    }

    [PostgresFact]
    public async Task Deleting_the_logo_clears_it_and_is_idempotent()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}/logo";
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(Png, "image/png", "logo.png"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync(url)).StatusCode);
        Assert.False((await owner.Http.GetFromJsonAsync<CompanyDetails>($"/api/companies/{companyId}"))!.HasLogo);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync(url)).StatusCode); // nothing to remove
    }

    [PostgresFact]
    public async Task Replacing_the_logo_serves_the_new_file_and_type()
    {
        var (owner, companyId) = await api.NewCompanyAsync();
        var url = $"/api/companies/{companyId}/logo";
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(Png, "image/png", "first.png"))).StatusCode);

        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PutAsync(url, Upload(webp, "image/webp", "second.webp"))).StatusCode);

        var read = await owner.Http.GetAsync(url);
        Assert.Equal("image/webp", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(webp, await read.Content.ReadAsByteArrayAsync());
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType, string fileName)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static async Task AssertFileProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("file", out _));
    }
}
