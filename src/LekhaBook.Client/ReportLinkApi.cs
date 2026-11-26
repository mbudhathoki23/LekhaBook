using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class ReportLinkApi(LekhaBookApi api) : IReportLinkApi
{
    public Task<ApiResult<List<ReportLinkDto>>> GetReportLinksAsync(Guid companyId, Guid accountId) =>
        api.GetAsync<List<ReportLinkDto>>($"/api/companies/{companyId}/report-links?accountId={accountId}");

    public Task<ApiResult<CreatedReportLinkDto>> CreateReportLinkAsync(Guid companyId, CreateReportLinkRequest request) =>
        api.SendAsync<CreatedReportLinkDto>(HttpMethod.Post, $"/api/companies/{companyId}/report-links", request);

    public Task<ApiResult<bool>> RevokeReportLinkAsync(Guid companyId, Guid linkId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"/api/companies/{companyId}/report-links/{linkId}", null);

    public Task<ApiResult<ResolvedReportLinkDto>> ResolveReportLinkAsync(string token) =>
        api.SendAsync<ResolvedReportLinkDto>(HttpMethod.Post, "/api/report-links/resolve", new ResolveReportLinkRequest(token));
}
