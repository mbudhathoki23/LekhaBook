using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LekhaBook.Domain;

public enum BillingPlan
{
    Trial,
    ProMonthly,
    ProYearly,
    BusinessMonthly,
    BusinessYearly,
}

public enum SubscriptionStatus
{
    Trialing,
    Active,
    Expired,
    Cancelled,
}

public enum PaymentProvider
{
    ESewa,
    Khalti,
    Stripe,
    GooglePlay,
    Manual,
}

public enum PaymentStatus
{
    Pending,
    Completed,
    Failed,
    Refunded,
}

/// <summary>
/// Server-only subscription for an online Lekha Books user (one active record per user).
/// Offline Local Mode stays free forever on device SQLite; subscriptions govern Online Cloud features.
/// </summary>
public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public BillingPlan Plan { get; set; } = BillingPlan.Trial;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;

    public DateTime TrialEndsAtUtc { get; set; }

    public DateTime CurrentPeriodStartUtc { get; set; }

    public DateTime CurrentPeriodEndUtc { get; set; }

    public PaymentProvider? LastProvider { get; set; }

    public string? ProviderCustomerId { get; set; }

    public string? ProviderSubscriptionId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Server-only ledger of checkout attempts and verified payments (idempotent by TransactionRef).</summary>
public sealed class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public Guid SubscriptionId { get; set; }

    public BillingPlan Plan { get; set; }

    public PaymentProvider Provider { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "NPR";

    /// <summary>Unique merchant transaction reference sent to eSewa / Khalti / Stripe.</summary>
    public required string TransactionRef { get; set; }

    /// <summary>Gateway reference returned after checkout (eSewa transaction_code, Khalti pidx, Stripe session_id, Play token).</summary>
    public string? ProviderRef { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}

public sealed record BillingPlanSpec(
    BillingPlan Plan,
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

/// <summary>Central pricing, entitlements, and renewal rules for Lekha Books Cloud.</summary>
public static class BillingCatalog
{
    public const int TrialDays = 14;

    public static readonly IReadOnlyList<BillingPlanSpec> PaidPlans =
    [
        new(BillingPlan.ProMonthly, "ProMonthly", "Pro Cloud", "Monthly", 299m, 4.99m, 30, MaxCompanies: 3, MaxStaffPerCompany: 1, IncludesStaff: false, IncludesPublicShareLinks: true),
        new(BillingPlan.ProYearly, "ProYearly", "Pro Cloud", "Yearly", 2499m, 39.00m, 365, MaxCompanies: 3, MaxStaffPerCompany: 1, IncludesStaff: false, IncludesPublicShareLinks: true),
        new(BillingPlan.BusinessMonthly, "BusinessMonthly", "Business Team", "Monthly", 699m, 12.00m, 30, MaxCompanies: 100, MaxStaffPerCompany: 10, IncludesStaff: true, IncludesPublicShareLinks: true),
        new(BillingPlan.BusinessYearly, "BusinessYearly", "Business Team", "Yearly", 5999m, 99.00m, 365, MaxCompanies: 100, MaxStaffPerCompany: 10, IncludesStaff: true, IncludesPublicShareLinks: true),
    ];

    public static readonly BillingPlanSpec TrialSpec =
        new(BillingPlan.Trial, "Trial", "14-Day Cloud Trial", "Trial", 0m, 0m, TrialDays, MaxCompanies: 3, MaxStaffPerCompany: 3, IncludesStaff: true, IncludesPublicShareLinks: true);

    public static BillingPlanSpec SpecFor(BillingPlan plan) =>
        plan == BillingPlan.Trial ? TrialSpec : PaidPlans.Single(p => p.Plan == plan);

    public static BillingPlan? ParsePaidPlan(string? code) =>
        PaidPlans.FirstOrDefault(p => string.Equals(p.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))?.Plan;

    public static PaymentProvider? ParseProvider(string? name) =>
        Enum.TryParse<PaymentProvider>(name?.Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : null;

    public static Subscription CreateTrial(Guid userId, DateTime nowUtc) => new()
    {
        UserId = userId,
        Plan = BillingPlan.Trial,
        Status = SubscriptionStatus.Trialing,
        TrialEndsAtUtc = nowUtc.AddDays(TrialDays),
        CurrentPeriodStartUtc = nowUtc,
        CurrentPeriodEndUtc = nowUtc.AddDays(TrialDays),
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    public static bool IsActive(Subscription sub, DateTime nowUtc) =>
        sub.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active && nowUtc < sub.CurrentPeriodEndUtc;

    public static SubscriptionStatus EffectiveStatus(Subscription sub, DateTime nowUtc) =>
        sub.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active && nowUtc >= sub.CurrentPeriodEndUtc
            ? SubscriptionStatus.Expired
            : sub.Status;

    public static int DaysRemaining(Subscription sub, DateTime nowUtc)
    {
        if (!IsActive(sub, nowUtc)) return 0;
        return Math.Max(1, (int)Math.Ceiling((sub.CurrentPeriodEndUtc - nowUtc).TotalDays));
    }

    /// <summary>
    /// Activates or extends a subscription after a verified payment. Early renewals stack on top of
    /// the remaining paid period so customers never lose days they already paid for.
    /// </summary>
    public static void ApplyPaidPlan(Subscription sub, BillingPlan paidPlan, PaymentProvider provider, DateTime nowUtc)
    {
        var spec = SpecFor(paidPlan);
        var anchor = sub.Status == SubscriptionStatus.Active && sub.CurrentPeriodEndUtc > nowUtc
            ? sub.CurrentPeriodEndUtc
            : nowUtc;

        sub.Plan = paidPlan;
        sub.Status = SubscriptionStatus.Active;
        sub.LastProvider = provider;
        sub.CurrentPeriodStartUtc = nowUtc;
        sub.CurrentPeriodEndUtc = anchor.AddDays(spec.DurationDays);
        sub.UpdatedAtUtc = nowUtc;
    }

    public static string? CheckCloudWriteAccess(Subscription sub, DateTime nowUtc) =>
        IsActive(sub, nowUtc) ? null : "Your Online Cloud subscription has expired. Renew your plan in Billing to continue syncing.";

    public static string? CheckCreateCompany(Subscription sub, int ownedCompanyCount, DateTime nowUtc)
    {
        if (!IsActive(sub, nowUtc))
        {
            return "Your Online Cloud subscription has expired. Renew your plan in Billing to add a business.";
        }

        var spec = SpecFor(sub.Plan);
        return ownedCompanyCount >= spec.MaxCompanies
            ? $"Your {spec.Name} plan supports up to {spec.MaxCompanies} business(es). Upgrade to Business Team for unlimited businesses."
            : null;
    }

    public static string? CheckInviteStaff(Subscription sub, int activeMemberCount, DateTime nowUtc)
    {
        if (!IsActive(sub, nowUtc))
        {
            return "Your Online Cloud subscription has expired. Renew your plan in Billing to invite staff.";
        }

        var spec = SpecFor(sub.Plan);
        if (!spec.IncludesStaff)
        {
            return "Staff accounts require the Business Team plan. Upgrade in Billing to invite your team.";
        }

        return activeMemberCount >= spec.MaxStaffPerCompany
            ? $"Your {spec.Name} plan supports up to {spec.MaxStaffPerCompany} team members per business."
            : null;
    }
}

/// <summary>
/// Cryptographic helpers for eSewa ePay v2 and Stripe webhook verification (constant-time HMAC-SHA256).
/// </summary>
public static class PaymentSignatures
{
    public static string ComputeHmacSha256Base64(string message, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    public static string ComputeHmacSha256Hex(string message, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    public static string ESewaCheckoutSignature(decimal totalAmount, string transactionUuid, string productCode, string secretKey)
    {
        var amount = FormatESewaAmount(totalAmount);
        var message = $"total_amount={amount},transaction_uuid={transactionUuid},product_code={productCode}";
        return ComputeHmacSha256Base64(message, secretKey);
    }

    public static string FormatESewaAmount(decimal amount) =>
        amount == Math.Truncate(amount)
            ? Math.Truncate(amount).ToString("0", CultureInfo.InvariantCulture)
            : amount.ToString("0.00", CultureInfo.InvariantCulture);

    public sealed record ESewaDecodedPayload(
        string TransactionUuid,
        string TransactionCode,
        string Status,
        string TotalAmount,
        string ProductCode,
        bool SignatureValid);

    /// <summary>Decodes eSewa's Base64 callback payload and validates its HMAC-SHA256 signature in constant time.</summary>
    public static ESewaDecodedPayload? ParseAndVerifyESewaCallback(string base64Data, string secretKey)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Data.Trim()));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string GetStr(string name) =>
                root.TryGetProperty(name, out var p)
                    ? (p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : p.GetRawText())
                    : string.Empty;

            var signedFields = GetStr("signed_field_names");
            var signature = GetStr("signature");
            if (string.IsNullOrWhiteSpace(signedFields) || string.IsNullOrWhiteSpace(signature)) return null;

            var parts = signedFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => $"{f}={GetStr(f)}");
            var message = string.Join(',', parts);
            var expected = ComputeHmacSha256Base64(message, secretKey);

            var valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature));

            return new ESewaDecodedPayload(
                TransactionUuid: GetStr("transaction_uuid"),
                TransactionCode: GetStr("transaction_code"),
                Status: GetStr("status"),
                TotalAmount: GetStr("total_amount"),
                ProductCode: GetStr("product_code"),
                SignatureValid: valid);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Validates Stripe's `Stripe-Signature` webhook header (`t=...,v1=...`) within a 5-minute replay window.</summary>
    public static bool VerifyStripeWebhookSignature(string payload, string? header, string webhookSecret, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(webhookSecret)) return false;

        long timestamp = 0;
        var v1Signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var k = part[..idx];
            var v = part[(idx + 1)..];
            if (k == "t" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                timestamp = parsed;
            }
            else if (k == "v1")
            {
                v1Signatures.Add(v);
            }
        }

        if (timestamp <= 0 || v1Signatures.Count == 0) return false;

        var age = Math.Abs((nowUtc - DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime).TotalSeconds);
        if (age > 300) return false;

        var expected = ComputeHmacSha256Hex($"{timestamp}.{payload}", webhookSecret);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return v1Signatures.Any(sig =>
        {
            var sigBytes = Encoding.UTF8.GetBytes(sig.ToLowerInvariant());
            return sigBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, sigBytes);
        });
    }
}
