using LekhaBook.Contracts;

namespace LekhaBook.UI.Services;

/// <summary>Online Cloud subscription status, checkout (eSewa / Khalti / Stripe), and payment verification.</summary>
public interface IBillingApi
{
    Task<ApiResult<BillingStatusResponse>> GetStatusAsync();

    Task<ApiResult<StartCheckoutResponse>> StartCheckoutAsync(StartCheckoutRequest request);

    Task<ApiResult<VerifyPaymentResponse>> VerifyPaymentAsync(VerifyPaymentRequest request);
}
