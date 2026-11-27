using LekhaBook.Contracts;
using LekhaBook.UI.Services;

namespace LekhaBook.Client;

public sealed class BillingApi(LekhaBookApi api) : IBillingApi
{
    public Task<ApiResult<BillingStatusResponse>> GetStatusAsync() =>
        api.GetAsync<BillingStatusResponse>("/api/billing/status");

    public Task<ApiResult<StartCheckoutResponse>> StartCheckoutAsync(StartCheckoutRequest request) =>
        api.SendAsync<StartCheckoutResponse>(HttpMethod.Post, "/api/billing/checkout", request);

    public Task<ApiResult<VerifyPaymentResponse>> VerifyPaymentAsync(VerifyPaymentRequest request) =>
        api.SendAsync<VerifyPaymentResponse>(HttpMethod.Post, "/api/billing/verify", request);
}
