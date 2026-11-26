using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

/// <summary>Public report links (decision D24).</summary>
public interface IReportLinkApi
{
    /// <summary>The links to one statement that still work.</summary>
    Task<ApiResult<List<ReportLinkDto>>> GetReportLinksAsync(Guid companyId, Guid accountId);

    Task<ApiResult<CreatedReportLinkDto>> CreateReportLinkAsync(Guid companyId, CreateReportLinkRequest request);

    Task<ApiResult<bool>> RevokeReportLinkAsync(Guid companyId, Guid linkId);

    /// <summary>Where the shared statement is in the app; StatusCode 404 when it isn't one of the user's.</summary>
    Task<ApiResult<ResolvedReportLinkDto>> ResolveReportLinkAsync(string token);
}
