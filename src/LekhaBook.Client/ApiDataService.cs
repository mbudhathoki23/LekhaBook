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

    // Transactions (Phase 6)
    public Task<ApiResult<List<TransactionDto>>> GetTransactionsAsync(Guid companyId, Guid? accountId = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var query = new List<string>();
        if (accountId is { } aid) query.Add($"accountId={aid}");
        if (fromDate is { } fd) query.Add($"fromDate={fd:yyyy-MM-dd}");
        if (toDate is { } td) query.Add($"toDate={td:yyyy-MM-dd}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        return api.GetAsync<List<TransactionDto>>($"{Company(companyId)}/transactions{qs}");
    }

    public Task<ApiResult<TransactionDto>> GetTransactionAsync(Guid companyId, Guid transactionId) =>
        api.GetAsync<TransactionDto>($"{Company(companyId)}/transactions/{transactionId}");

    public Task<ApiResult<TransactionDto>> AddTransactionAsync(Guid companyId, CreateTransactionRequest request) =>
        api.SendAsync<TransactionDto>(HttpMethod.Post, $"{Company(companyId)}/transactions", request with { Id = request.Id ?? Guid.CreateVersion7() });

    public Task<ApiResult<TransactionDto>> UpdateTransactionAsync(Guid companyId, Guid transactionId, UpdateTransactionRequest request) =>
        api.SendAsync<TransactionDto>(HttpMethod.Put, $"{Company(companyId)}/transactions/{transactionId}", request);

    public Task<ApiResult<bool>> DeleteTransactionAsync(Guid companyId, Guid transactionId) =>
        api.SendAsync<bool>(HttpMethod.Delete, $"{Company(companyId)}/transactions/{transactionId}", null);

    public Task<ApiResult<LedgerDto>> GetTransactionLedgerAsync(Guid companyId, Guid transactionId) =>
        api.GetAsync<LedgerDto>($"{Company(companyId)}/transactions/{transactionId}/ledger");
}
