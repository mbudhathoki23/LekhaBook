using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

/// <summary>Balance adjustments and new expense or income heads.</summary>
public interface IBookExtrasApi
{
    /// <summary>Adds to or reduces a party's or money account's balance.</summary>
    Task<ApiResult<bool>> AdjustBalanceAsync(Guid companyId, Guid accountId, AdjustBalanceRequest request);

    Task<ApiResult<CategoryDto>> AddCategoryAsync(Guid companyId, SaveCategoryRequest request);
}
