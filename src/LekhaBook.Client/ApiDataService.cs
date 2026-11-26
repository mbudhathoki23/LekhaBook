using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

/// <summary>Books data straight from the server: the browser always, the MAUI app until device sync lands (D14).</summary>
public sealed class ApiDataService(LekhaBookApi api) : IDataService
{
    private static string Company(Guid companyId) => $"/api/companies/{companyId}";

    public Task<ApiResult<List<PartyDto>>> GetPartiesAsync(Guid companyId) =>
        api.GetAsync<List<PartyDto>>($"{Company(companyId)}/parties");

    public Task<ApiResult<PartyDto>> GetPartyAsync(Guid companyId, Guid partyId) =>
        api.GetAsync<PartyDto>($"{Company(companyId)}/parties/{partyId}");

    public Task<ApiResult<PartyDto>> AddPartyAsync(Guid companyId, SavePartyRequest request) =>
        api.SendAsync<PartyDto>(HttpMethod.Post, $"{Company(companyId)}/parties", request with { Id = request.Id ?? Guid.CreateVersion7() });

    public Task<ApiResult<PartyDto>> UpdatePartyAsync(Guid companyId, Guid partyId, SavePartyRequest request) =>
        api.SendAsync<PartyDto>(HttpMethod.Put, $"{Company(companyId)}/parties/{partyId}", request);

    public Task<ApiResult<bool>> DeletePartyAsync(Guid companyId, Guid partyId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"{Company(companyId)}/parties/{partyId}", null);

    public Task<ApiResult<LedgerDto>> GetPartyLedgerAsync(Guid companyId, Guid partyId) =>
        api.GetAsync<LedgerDto>($"{Company(companyId)}/parties/{partyId}/ledger");

    public Task<ApiResult<List<PartyCategoryDto>>> GetPartyCategoriesAsync(Guid companyId) =>
        api.GetAsync<List<PartyCategoryDto>>($"{Company(companyId)}/party-categories");

    public Task<ApiResult<PartyCategoryDto>> AddPartyCategoryAsync(Guid companyId, SavePartyCategoryRequest request) =>
        api.SendAsync<PartyCategoryDto>(HttpMethod.Post, $"{Company(companyId)}/party-categories", request with { Id = request.Id ?? Guid.CreateVersion7() });

    public Task<ApiResult<List<MoneyAccountDto>>> GetMoneyAccountsAsync(Guid companyId) =>
        api.GetAsync<List<MoneyAccountDto>>($"{Company(companyId)}/accounts");

    public Task<ApiResult<MoneyAccountDto>> GetMoneyAccountAsync(Guid companyId, Guid accountId) =>
        api.GetAsync<MoneyAccountDto>($"{Company(companyId)}/accounts/{accountId}");

    public Task<ApiResult<MoneyAccountDto>> AddMoneyAccountAsync(Guid companyId, SaveMoneyAccountRequest request) =>
        api.SendAsync<MoneyAccountDto>(HttpMethod.Post, $"{Company(companyId)}/accounts", request with { Id = request.Id ?? Guid.CreateVersion7() });

    public Task<ApiResult<MoneyAccountDto>> UpdateMoneyAccountAsync(Guid companyId, Guid accountId, SaveMoneyAccountRequest request) =>
        api.SendAsync<MoneyAccountDto>(HttpMethod.Put, $"{Company(companyId)}/accounts/{accountId}", request);

    public Task<ApiResult<bool>> DeleteMoneyAccountAsync(Guid companyId, Guid accountId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"{Company(companyId)}/accounts/{accountId}", null);

    public Task<ApiResult<LedgerDto>> GetAccountLedgerAsync(Guid companyId, Guid accountId) =>
        api.GetAsync<LedgerDto>($"{Company(companyId)}/accounts/{accountId}/ledger");

    public Task<ApiResult<List<CategoryDto>>> GetCategoriesAsync(Guid companyId) =>
        api.GetAsync<List<CategoryDto>>($"{Company(companyId)}/categories");

    public Task<ApiResult<List<TransactionDto>>> GetTransactionsAsync(Guid companyId) =>
        api.GetAsync<List<TransactionDto>>($"{Company(companyId)}/transactions");

    public Task<ApiResult<TransactionDto>> GetTransactionAsync(Guid companyId, Guid transactionId) =>
        api.GetAsync<TransactionDto>($"{Company(companyId)}/transactions/{transactionId}");

    public Task<ApiResult<TransactionDto>> AddTransactionAsync(Guid companyId, CreateTransactionRequest request) =>
        api.SendAsync<TransactionDto>(HttpMethod.Post, $"{Company(companyId)}/transactions", request with { Id = request.Id ?? Guid.CreateVersion7() });

    public Task<ApiResult<TransactionDto>> UpdateTransactionAsync(Guid companyId, Guid transactionId, UpdateTransactionRequest request) =>
        api.SendAsync<TransactionDto>(HttpMethod.Put, $"{Company(companyId)}/transactions/{transactionId}", request);

    public Task<ApiResult<bool>> DeleteTransactionAsync(Guid companyId, Guid transactionId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"{Company(companyId)}/transactions/{transactionId}", null);
}

/// <summary>Attachments via the API (Phase 7).</summary>
public sealed class ApiAttachmentService(LekhaBookApi api) : IAttachmentService
{
    private static string Company(Guid companyId) => $"/api/companies/{companyId}";

    public Task<ApiResult<AttachmentDto>> CapturePhotoAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<AttachmentDto>(null, "Capture requires platform camera. Use UploadAttachmentAsync with an InputFile.", 0));

    public Task<ApiResult<AttachmentDto>> PickAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default) =>
        Task.FromResult(new ApiResult<AttachmentDto>(null, "Pick requires platform UI. Use UploadAttachmentAsync with an InputFile.", 0));

    public Task<ApiResult<AttachmentFile>> GetContentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default) =>
        api.GetFileAsync($"{Company(companyId)}/attachments/{attachmentId}");

    public async Task<ApiResult<AttachmentDto>> UploadAttachmentAsync(Guid companyId, Stream fileStream, string fileName, string contentType, Guid? transactionId = null, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        if (transactionId.HasValue)
        {
            content.Add(new StringContent(transactionId.Value.ToString()), "transactionId");
        }
        return await api.SendContentAsync<AttachmentDto>(HttpMethod.Post, $"{Company(companyId)}/attachments", content);
    }

    public Task<ApiResult<List<AttachmentDto>>> GetAttachmentsAsync(Guid companyId, Guid? transactionId = null, CancellationToken ct = default)
    {
        var qs = transactionId.HasValue ? $"?transactionId={transactionId.Value}" : "";
        return api.GetAsync<List<AttachmentDto>>($"{Company(companyId)}/attachments{qs}");
    }

    public Task<ApiResult<bool>> DeleteAttachmentAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"{Company(companyId)}/attachments/{attachmentId}", null);
}
