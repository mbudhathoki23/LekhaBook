namespace LekhaBook.Contracts;

public sealed record BillingPlanDto(
    string Code,
    string Name,
    string BillingCycle,
    decimal PriceNpr,
    decimal PriceUsd,
    int DurationDays,
    int MaxCompanies,
    int MaxStaffPerCompany,
    bool IncludesStaff,
    bool IncludesPublicShareLinks);

public sealed record PaymentRecordDto(
    Guid Id,
    string Plan,
    string Provider,
    string Status,
    decimal Amount,
    string Currency,
    string TransactionRef,
    string? ProviderRef,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record BillingProviderAvailabilityDto(
    bool ESewa,
    bool Khalti,
    bool Stripe,
    bool GooglePlay,
    bool SandboxTestMode);

public sealed record BillingStatusResponse(
    string Plan,
    string PlanName,
    string Status,
    bool IsActive,
    int DaysRemaining,
    DateTime CurrentPeriodEndUtc,
    int MaxCompanies,
    int MaxStaffPerCompany,
    bool IncludesStaff,
    bool EnforcementEnabled,
    BillingProviderAvailabilityDto Providers,
    IReadOnlyList<BillingPlanDto> Plans,
    IReadOnlyList<PaymentRecordDto> History);

public sealed record StartCheckoutRequest(
    string PlanCode,
    string Provider,
    string? ReturnBaseUrl = null);

/// <summary>
/// Response from POST /api/billing/checkout:
/// - For Khalti / Stripe: navigate to <paramref name="RedirectUrl"/>.
/// - For eSewa ePay v2: POST <paramref name="FormFields"/> to <paramref name="FormActionUrl"/>.
/// - For Sandbox/Manual test activation: <paramref name="CompletedImmediately"/> is true.
/// </summary>
public sealed record StartCheckoutResponse(
    Guid PaymentId,
    string TransactionRef,
    string Provider,
    string? RedirectUrl = null,
    string? FormActionUrl = null,
    IReadOnlyDictionary<string, string>? FormFields = null,
    bool CompletedImmediately = false);

public sealed record VerifyPaymentRequest(
    string Provider,
    string? TransactionRef = null,
    string? ESewaData = null,
    string? KhaltiPidx = null,
    string? StripeSessionId = null,
    string? GooglePlayPurchaseToken = null,
    string? GooglePlayProductId = null);

public sealed record VerifyPaymentResponse(
    bool Activated,
    string Message,
    BillingStatusResponse Status);
