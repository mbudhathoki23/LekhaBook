using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class BookExtrasApi(LekhaBookApi api) : IBookExtrasApi
{
    public Task<ApiResult<bool>> AdjustBalanceAsync(Guid companyId, Guid accountId, AdjustBalanceRequest request) =>
        api.SendAsync<bool>(HttpMethod.Post, $"/api/companies/{companyId}/accounts/{accountId}/adjustments", request);

    public Task<ApiResult<CategoryDto>> AddCategoryAsync(Guid companyId, SaveCategoryRequest request) =>
        api.SendAsync<CategoryDto>(HttpMethod.Post, $"/api/companies/{companyId}/categories", request);
}
