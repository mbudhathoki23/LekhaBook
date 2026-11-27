using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class CompanyApi(LekhaBookApi api) : ICompanyApi
{
    public Task<ApiResult<CompanySummary>> CreateCompanyAsync(CreateCompanyRequest request) =>
        api.SendAsync<CompanySummary>(HttpMethod.Post, "/api/companies", request);

    public Task<ApiResult<MigrateOfflineCompanyResponse>> MigrateOfflineCompanyAsync(MigrateOfflineCompanyRequest request) =>
        api.SendAsync<MigrateOfflineCompanyResponse>(HttpMethod.Post, "/api/companies/migrate", request);

    public Task<ApiResult<bool>> UpdateCalendarAsync(Guid companyId, string calendar) =>
        api.SendAsync<bool>(HttpMethod.Put, $"/api/companies/{companyId}/calendar", new UpdateCalendarRequest(calendar));

    public Task<ApiResult<List<MemberDto>>> GetMembersAsync(Guid companyId) =>
        api.GetAsync<List<MemberDto>>($"/api/companies/{companyId}/members");

    public Task<ApiResult<MemberDto>> UpdateMemberAsync(Guid companyId, Guid memberId, UpdateMemberRequest request) =>
        api.SendAsync<MemberDto>(HttpMethod.Put, $"/api/companies/{companyId}/members/{memberId}", request);

    public Task<ApiResult<bool>> RevokeMemberAsync(Guid companyId, Guid memberId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"/api/companies/{companyId}/members/{memberId}", null);

    public Task<ApiResult<List<InvitationDto>>> GetInvitationsAsync(Guid companyId) =>
        api.GetAsync<List<InvitationDto>>($"/api/companies/{companyId}/invitations");

    public Task<ApiResult<InviteResponse>> InviteAsync(Guid companyId, InviteRequest request) =>
        api.SendAsync<InviteResponse>(HttpMethod.Post, $"/api/companies/{companyId}/invitations", request);

    public Task<ApiResult<bool>> CancelInvitationAsync(Guid companyId, Guid invitationId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"/api/companies/{companyId}/invitations/{invitationId}", null);

    public Task<ApiResult<CompanySummary>> AcceptInvitationAsync(string token) =>
        api.SendAsync<CompanySummary>(HttpMethod.Post, "/api/invitations/accept", new AcceptInvitationRequest(token.Trim()));

    public Task<ApiResult<CompanyDetails>> GetCompanyAsync(Guid companyId) =>
        api.GetAsync<CompanyDetails>($"/api/companies/{companyId}");

    public Task<ApiResult<AttachmentFile>> GetLogoAsync(Guid companyId) =>
        api.GetFileAsync($"/api/companies/{companyId}/logo");

    public async Task<ApiResult<bool>> UploadLogoAsync(Guid companyId, Stream fileStream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return await api.SendContentAsync<bool>(HttpMethod.Put, $"/api/companies/{companyId}/logo", content);
    }

    public Task<ApiResult<bool>> DeleteLogoAsync(Guid companyId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"/api/companies/{companyId}/logo", null);
}
