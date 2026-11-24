using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class CompanyApi(LekhaBookApi api) : ICompanyApi
{
    public Task<ApiResult<CompanySummary>> CreateCompanyAsync(CreateCompanyRequest request) =>
        api.SendAsync<CompanySummary>(HttpMethod.Post, "/api/companies", request);

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
}
